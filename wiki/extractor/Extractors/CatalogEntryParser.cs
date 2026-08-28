using System.Text;
using System.Text.Json;

namespace HHWiki.Extractor.Extractors;

/// <summary>
/// Proper parser for Unity Addressables' ContentCatalogData binary format.
///
/// Replaces the earlier "adjacent keys" heuristic in Parse-Recipes.ps1 with a real reader.
/// Unity structures the catalog as:
///
///   KeyDataString    : array of keys, each [byte type][data]
///                      Types: 1=AsciiString, 2=UnicodeString, 6=Hash128, plus int/float variants.
///                      A single asset entry has multiple keys (a Hash128 GUID + one or more
///                      string labels). Keys are NOT grouped by entry in KeyDataString - grouping
///                      happens via BucketDataString.
///
///   EntryDataString  : array of entries, each 28 bytes:
///                      [internalIdIndex][providerIndex][dependencyKeyOffset][depHash]
///                      [dataOffset][primaryKeyOffset][resourceTypeIndex]
///                      primaryKeyOffset points into KeyDataString and gives the entry's canonical name.
///
///   BucketDataString : array of buckets (one per key), each:
///                      [keyOffsetInKeyDataString][entryCount][entryIndex...]
///                      Two keys with the same entryIndex set are aliases for the same asset.
///
/// The output is a GUID-to-canonical-name map that Parse-Recipes.ps1 consumes.
/// </summary>
public static class CatalogEntryParser
{
    public sealed record Key(int Offset, byte Type, string Value);

    public sealed record Entry(int InternalIdIndex, int PrimaryKeyOffset, int ResourceTypeIndex);

    // Unity Addressables ObjectType enum (verified by parsing the game's catalog):
    // 0=AsciiString, 1=UnicodeString, 2=UInt16, 3=UInt32, 4=Int32, 5=Hash128, 6=Type, 7=JsonObject
    // In this game's catalog, GUIDs are stored as AsciiString (type 0), 32-char hex - not as
    // Hash128. So we detect "GUID-ness" by the string value, not the type byte.
    private const byte TYPE_ASCII    = 0;
    private const byte TYPE_UNICODE  = 1;
    private const byte TYPE_UINT16   = 2;
    private const byte TYPE_UINT32   = 3;
    private const byte TYPE_INT32    = 4;
    private const byte TYPE_HASH128  = 5;

    public sealed record Result(
        int TotalKeys,
        int TotalEntries,
        int GuidCount,
        Dictionary<string, string> GuidToName);

    public static Result Parse(string catalogJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogJsonPath));
        var keyData = Convert.FromBase64String(doc.RootElement.GetProperty("m_KeyDataString").GetString()!);
        var entryData = Convert.FromBase64String(doc.RootElement.GetProperty("m_EntryDataString").GetString()!);
        var bucketData = Convert.FromBase64String(doc.RootElement.GetProperty("m_BucketDataString").GetString()!);

        var keys = ParseKeys(keyData);
        var entries = ParseEntries(entryData);
        var buckets = ParseBuckets(bucketData);

        // Map from KeyDataString byte offset -> Key object
        var keyByOffset = keys.ToDictionary(k => k.Offset);

        // Build inverse index: entryIndex -> list of ALL keys pointing to it (aliases).
        // Two keys with the same entry are aliases for the same asset.
        var entryToKeys = new Dictionary<int, List<Key>>();
        foreach (var bucket in buckets)
        {
            if (!keyByOffset.TryGetValue(bucket.KeyOffset, out var key)) continue;
            foreach (var entryIdx in bucket.EntryIndexes)
            {
                if (!entryToKeys.TryGetValue(entryIdx, out var list))
                {
                    list = new List<Key>();
                    entryToKeys[entryIdx] = list;
                }
                list.Add(key);
            }
        }

        // For each entry, find the most-human-readable key label:
        //   1. Non-GUID string with a known asset prefix (BF_, BO_, RII_, etc.)
        //   2. Non-GUID string with an underscore
        //   3. Any non-GUID string
        //   4. Fallback: the GUID itself
        var entryLabel = new Dictionary<int, string>();
        foreach (var (idx, keyList) in entryToKeys)
        {
            var readable = keyList.Where(k => !IsGuidLike(k.Value)).ToList();
            string label;
            if (readable.Count > 0)
            {
                var withPrefix = readable.FirstOrDefault(k => LooksLikeAssetId(k.Value));
                label = withPrefix?.Value ?? readable.OrderBy(k => k.Value.Length).First().Value;
            }
            else
            {
                label = keyList[0].Value;
            }
            entryLabel[idx] = label;
        }

        // Build GUID -> canonical-name map by walking every GUID-shaped key and looking at
        // the aliases sharing the same entry.
        var guidToName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            if (!keyByOffset.TryGetValue(bucket.KeyOffset, out var key)) continue;
            if (!IsGuidLike(key.Value)) continue;
            foreach (var entryIdx in bucket.EntryIndexes)
            {
                if (!entryLabel.TryGetValue(entryIdx, out var label)) continue;
                if (IsGuidLike(label)) continue;
                guidToName[key.Value] = label;
                break;
            }
        }

        return new Result(keys.Count, entries.Count, guidToName.Count, guidToName);
    }

    private static bool IsGuidLike(string s) =>
        s.Length == 32 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static readonly string[] KnownPrefixes =
    {
        "BF_", "BFI_", "BO_", "BOI_", "BT_", "BTI_", "BW_", "BWI_",
        "RII_", "FWI_", "AXE_", "DAG_", "SPEAR_", "BIG_", "TOOL_", "RW_", "BHC_", "WB_",
        "Bullet_", "Ore_", "AA_", "PAD_", "FZ_", "APC_", "CH_", "CBU_"
    };

    private static bool LooksLikeAssetId(string s) =>
        KnownPrefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal)) || s.Contains('_');

    // -----------------------------------------------------------------------
    // KeyDataString: [int keyCount] then for each key: [byte type][data]
    // -----------------------------------------------------------------------
    private static List<Key> ParseKeys(byte[] data)
    {
        var keys = new List<Key>();
        var count = BitConverter.ToInt32(data, 0);
        var offset = 4;
        for (int i = 0; i < count; i++)
        {
            var keyStart = offset;
            if (offset >= data.Length) break;
            var type = data[offset++];
            string value;
            switch (type)
            {
                case TYPE_ASCII:
                    {
                        var len = BitConverter.ToInt32(data, offset); offset += 4;
                        value = Encoding.ASCII.GetString(data, offset, len);
                        offset += len;
                        break;
                    }
                case TYPE_UNICODE:
                    {
                        var len = BitConverter.ToInt32(data, offset); offset += 4;
                        value = Encoding.Unicode.GetString(data, offset, len);
                        offset += len;
                        break;
                    }
                case TYPE_UINT16:
                    value = BitConverter.ToUInt16(data, offset).ToString();
                    offset += 2;
                    break;
                case TYPE_UINT32:
                    value = BitConverter.ToUInt32(data, offset).ToString();
                    offset += 4;
                    break;
                case TYPE_INT32:
                    value = BitConverter.ToInt32(data, offset).ToString();
                    offset += 4;
                    break;
                case TYPE_HASH128:
                    {
                        // Length-prefixed by 1 byte then ASCII hex
                        var len = data[offset++];
                        value = Encoding.ASCII.GetString(data, offset, len);
                        offset += len;
                        break;
                    }
                default:
                    // Unknown type - abort to avoid runaway parsing
                    return keys;
            }
            keys.Add(new Key(keyStart, type, value));
        }
        return keys;
    }

    // -----------------------------------------------------------------------
    // EntryDataString: [int entryCount] then 28 bytes per entry
    // -----------------------------------------------------------------------
    private static List<Entry> ParseEntries(byte[] data)
    {
        var entries = new List<Entry>();
        var count = BitConverter.ToInt32(data, 0);
        var offset = 4;
        for (int i = 0; i < count && offset + 28 <= data.Length; i++)
        {
            var internalIdIndex = BitConverter.ToInt32(data, offset + 0);
            // providerIndex     = BitConverter.ToInt32(data, offset + 4);
            // dependencyKeyOff  = BitConverter.ToInt32(data, offset + 8);
            // depHash           = BitConverter.ToInt32(data, offset + 12);
            // dataOffset        = BitConverter.ToInt32(data, offset + 16);
            var primaryKeyOff   = BitConverter.ToInt32(data, offset + 20);
            var resourceTypeIdx = BitConverter.ToInt32(data, offset + 24);
            entries.Add(new Entry(internalIdIndex, primaryKeyOff, resourceTypeIdx));
            offset += 28;
        }
        return entries;
    }

    // -----------------------------------------------------------------------
    // BucketDataString: [int bucketCount], each bucket: [int keyOffset][int entryCount][int entryIndex...]
    // -----------------------------------------------------------------------
    public sealed record Bucket(int KeyOffset, List<int> EntryIndexes);

    private static List<Bucket> ParseBuckets(byte[] data)
    {
        var buckets = new List<Bucket>();
        var count = BitConverter.ToInt32(data, 0);
        var offset = 4;
        for (int i = 0; i < count && offset + 8 <= data.Length; i++)
        {
            var keyOff = BitConverter.ToInt32(data, offset); offset += 4;
            var entryCount = BitConverter.ToInt32(data, offset); offset += 4;
            var indexes = new List<int>(entryCount);
            for (int j = 0; j < entryCount && offset + 4 <= data.Length; j++)
            {
                indexes.Add(BitConverter.ToInt32(data, offset));
                offset += 4;
            }
            buckets.Add(new Bucket(keyOff, indexes));
        }
        return buckets;
    }
}

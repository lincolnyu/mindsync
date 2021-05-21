using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace mindsync
{
    class Program
    {
        static string Download(string url)
        {
            var b = new WebClient().DownloadData(url);
            return Encoding.UTF8.GetString(b);
        }
        class Item
        {
            public string Id { get; set; }
            public string Message { get;set; }
            public bool Pinned { get; set; }
            public string TimeCreated { get; set; }
            public string TimeUpdated { get; set; }
        }
        class ItemOrderComparer : IComparer<Item>
        {
            public static readonly ItemOrderComparer Instance = new ItemOrderComparer();
            public int Compare(Item x, Item y)
            {
                var c = x.Id.Length.CompareTo(y.Id.Length);
                if (c != 0) return c;
                return x.Id.CompareTo(y.Id);
            }
        }

        class Downloader
        {
            public const int DefaultMaxLen = 30000;
            private const string TitlePrefix = "**MINDSYNC-";
            private const string TitleSuffix = "**"; 
            string _userId;
            Dictionary<string, Item> _items = new Dictionary<string, Item>(); 
            public Downloader(string userId)
            {
                _userId = userId;
            }
            public void Deserialize(StreamReader sr)
            {
                // not generic yaml parsing
                _items.Clear();

                string id = null;
                var message = new StringBuilder();
                while (!sr.EndOfStream)
                {
                    var line = sr.ReadLine();
                    if (line.StartsWith(TitlePrefix) && line.EndsWith(TitleSuffix))
                    {
                        if (id != null)
                        {
                            var item = new Item
                            {
                                Id = id,
                                Message = message.ToString()
                            };
                            _items[id] = item;
                            message.Clear();
                        }
                        var pre = TitlePrefix.Length;
                        var post = TitleSuffix.Length;
                        id = line.Substring(pre, line.Length-post-pre);
                    }
                    else
                    {
                        message.AppendLine(line);
                    }
                }
                if (id != null)
                {
                    var item = new Item
                    {
                        Id = id,
                        Message = message.ToString()
                    };
                    _items[id] = item;
                }
            }
            public void Serialize(StreamWriter sw)
            {
                foreach (var (_,item) in _items.OrderBy(kvp=>kvp.Value, ItemOrderComparer.Instance).Reverse())
                {
                    sw.WriteLine($"{TitlePrefix}{item.Id}{TitleSuffix}");
                    sw.WriteLine($"{item.Message}");
                }
            }
            private Item GetEntity(JsonElement je, int? index)
            {
                var entities = je.GetProperty("entities");
                var isIndividual = !index.HasValue;
                if (isIndividual)
                {
                    index = 0;
                }
                var entity = entities[index.Value];
                var item = new Item
                {
                    // Note this can be the original mind and is different than the remind id
                    Id = entity.GetProperty("guid").ToString()
                };
                JsonElement entityContent = default(JsonElement); 
                if (isIndividual)
                {
                    entityContent = entity;
                }
                if ((isIndividual || entity.TryGetProperty("entity", out entityContent))
                    && entityContent.ValueKind == JsonValueKind.Object)
                {
                    if (entityContent.TryGetProperty("message", out var msgVal) && msgVal.ValueKind == JsonValueKind.String)
                    {
                        item.Message = msgVal.GetString();
                    }
                    if (entityContent.TryGetProperty("pinned", out var pinnedVal))
                    {
                        item.Pinned = pinnedVal.ValueKind == JsonValueKind.True;
                    }
                    if (entityContent.TryGetProperty("time_created", out var timeCreatedVal) && timeCreatedVal.ValueKind == JsonValueKind.String)
                    {
                        item.TimeCreated = timeCreatedVal.GetString();
                    }
                    if (entityContent.TryGetProperty("time_updated", out var timeUpdatedVal) && timeUpdatedVal.ValueKind == JsonValueKind.String)
                    {
                        item.TimeUpdated = timeUpdatedVal.GetString();
                    }
                }
                return item;
            }
            public int UpdateList(int maxLen=DefaultMaxLen, bool force=false)
            {
                var url = $"https://www.minds.com/api/v2/feeds/container/{_userId}/activities?sync=1&limit={maxLen}";
                var json = Download(url);
                var jsdoc = JsonDocument.Parse(json);
                var len = jsdoc.RootElement.GetProperty("entities").GetArrayLength();
                var updateCount = 0;
                Parallel.For(0, len, (i, s)=>
                {
                    var item = GetEntity(jsdoc.RootElement, i);
                    var itemId = item.Id;
                    if (force || !_items.ContainsKey(item.Id))
                    {
                        if (item.Message == null)   // Not recent therefore not containing message. Have to open the entry
                        {
                            var msgUrl = $"https://www.minds.com/api/v2/entities/?urns=urn%3Aactivity%3A{item.Id}&as_activities=0&export_user_counts=false";
                            var msgJson = Download(msgUrl);
                            var msgJdoc = JsonDocument.Parse(msgJson);
                            item = GetEntity(msgJdoc.RootElement, null);
                            item.Id = itemId;   // overwrite the original with remind id
                        }
                        _items[itemId] = item;
                        Console.Write($"\rDownloaded '{item.Id}'");
                        Interlocked.Increment(ref updateCount);
                    }
                });
                if (updateCount > 0)
                {
                    Console.WriteLine();
                }
                Console.WriteLine($"{updateCount} items updated.");
                return updateCount;
            }
        }
        static void Main(string[] args)
        {
            string l_arg = null;
            bool force = false;
            string userId = "1197537175369949199";
            string outFile = "out.txt";
            foreach (var arg in args)
            {
                if (l_arg != null)
                {
                    if (l_arg == "-u")
                    {
                        userId = arg;
                    }
                    else if (l_arg == "-o")
                    {
                        outFile = arg;
                    }
                    l_arg = null;
                }
                else if (arg == "-u" || arg == "-o")
                {
                    l_arg = arg;
                }
                else if (arg == "-f")
                {
                    force = true;
                }
                else if (arg == "-h")
                {
                    Console.WriteLine("Usage: <app> [-f] [-u <userid>] [-O <outputfile>]");
                    Console.WriteLine("       <app> -h: Show this help.");
                    return;
                }
            }
            var dl = new Downloader(userId);
            if (File.Exists(outFile))
            {
                using var srSynced = new StreamReader(outFile);
                dl.Deserialize(srSynced);
            }
            if (dl.UpdateList(Downloader.DefaultMaxLen, force) > 0)
            {
                using var sw = new StreamWriter(outFile);
                dl.Serialize(sw);
            }
        }
    }
}

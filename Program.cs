﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
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
            public Downloader(string userId="1197537175369949199")
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
                        message.Clear();
                        message.AppendLine(line);
                    }
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
            public void UpdateList(int maxLen=DefaultMaxLen, bool force=false)
            {
                var url = $"https://www.minds.com/api/v2/feeds/container/{_userId}/activities?sync=1&limit={maxLen}";
                var json = Download(url);
                var jsdoc = JsonDocument.Parse(json);
                var len = jsdoc.RootElement.GetProperty("entities").GetArrayLength();
                Parallel.For(0, len, (i, s)=>
                {
                    var item = GetEntity(jsdoc.RootElement, i);
                    var hasIt = _items.TryGetValue(item.Id, out var existingItem);
                    if (force || !hasIt || string.IsNullOrWhiteSpace(existingItem.Message))
                    {
                        if (item.Message == null)
                        {
                            var msgUrl = $"https://www.minds.com/api/v2/entities/?urns=urn%3Aactivity%3A{item.Id}&as_activities=0&export_user_counts=false";
                            var msgJson = Download(msgUrl);
                            var msgJdoc = JsonDocument.Parse(msgJson);
                            item = GetEntity(msgJdoc.RootElement, null);
                        }
                        _items[item.Id] = item;
                        Console.Write($"\rDownloaded '{item.Id}'");
                    }
                });
                Console.WriteLine("\n");
            }
        }
        static void Main(string[] args)
        {
            var iforce = Array.IndexOf(args, "-f");
            var force = false;
            var fn = "out.txt";
            if (iforce >= 0)
            {
                force = true;
                if (iforce > 0)
                {
                    fn = args[0];
                }
                else if (args.Length > 1)
                {
                    fn = args[1];
                }
            }
            else if (args.Length > 0)
            {
                fn = args[0];
            }
            var dl = new Downloader();
            if (File.Exists(fn))
            {
                using var srSynced = new StreamReader(fn);
                dl.Deserialize(srSynced);
            }
            dl.UpdateList(Downloader.DefaultMaxLen, force);
            {
                using var sw = new StreamWriter(fn);
                dl.Serialize(sw);
            }
        }
    }
}

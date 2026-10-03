using System.Text.Json.Nodes;
using IsleBar.Core.Configuration;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var settings = new ConfigStore(dir.File("islebar.json")).Load();

        Assert.Equal("auto", settings.Lang);
        Assert.Equal("auto", settings.Values.Perm);         // fresh installs start on auto (10-01); the owner's file keeps bypass
        Assert.Equal("new", settings.Values.Session);
        Assert.Equal("default", settings.Values.Model);
        Assert.Equal("default", settings.Values.Effort);
        Assert.False(settings.Values.Rc);
        Assert.True(settings.Files);
        Assert.Equal("", settings.Folder);
        Assert.Empty(settings.History);
        Assert.Equal(LaunchOptionDefs.DefaultQuick, settings.Quick);
        Assert.Equal(LaunchOptionDefs.DefaultModels, settings.Models);
        Assert.Equal(0, settings.ModelsChecked);
    }

    [Theory]
    [InlineData("{ 이건 JSON이 아니다")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]          // JSON but not an object
    [InlineData("\"그냥 문자열\"")]
    [InlineData("null")]
    public void Broken_JSON_gives_defaults_and_keeps_the_file(string content)
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, content);

        var settings = new ConfigStore(path).Load();

        Assert.Equal("auto", settings.Lang);
        Assert.Equal("auto", settings.Values.Perm);
        Assert.True(File.Exists(path));                     // never delete the user's file
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Saving_over_a_broken_file_keeps_a_copy_of_it_first()
    {
        // A truncated settings file used to be replaced by defaults on the bar's first save — history and all gone (10-01).
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        const string broken = "{ \"drop_command\": \"pythonw send.py\", \"history\": [\"안녕";
        File.WriteAllText(path, broken);
        var store = new ConfigStore(path);

        Assert.True(store.Update(s => { s.Lang = "ko"; return true; }));

        var copy = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "islebar.json.broken-*"));
        Assert.Equal(broken, File.ReadAllText(copy));
        Assert.Equal("ko", store.Load().Lang);

        // Once the file is valid again, later saves don't make more copies
        Assert.True(store.Update(s => { s.Lang = "en"; return true; }));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "islebar.json.broken-*"));
    }

    [Fact]
    public void Only_keys_with_wrong_type_fall_back_and_the_rest_survive()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """
        {
          "lang": 42,
          "folder": "/home/ubuntu",
          "files": "yes",
          "history": "문자열인데 목록이어야 함",
          "quick": [1, 2, "model"],
          "models_checked": "어제",
          "values": { "session": true, "perm": "plan", "rc": 1, "effort": "high" }
        }
        """);

        var settings = new ConfigStore(path).Load();

        Assert.Equal("auto", settings.Lang);                 // number → default
        Assert.Equal("/home/ubuntu", settings.Folder);       // survives
        Assert.True(settings.Files);                         // "yes" isn't a bool → default
        Assert.Empty(settings.History);                      // not a list → default
        Assert.Equal(["model"], settings.Quick);             // only strings are kept
        Assert.Equal(0, settings.ModelsChecked);
        Assert.Equal("new", settings.Values.Session);        // true isn't a string → default
        Assert.Equal("plan", settings.Values.Perm);          // survives
        Assert.False(settings.Values.Rc);                    // 1 isn't a bool → default
        Assert.Equal("high", settings.Values.Effort);        // survives
    }

    [Fact]
    public void Value_not_in_choices_gives_default()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """
        { "lang": "sv", "values": { "perm": "nope", "session": "restart", "effort": "turbo" } }
        """);

        var settings = new ConfigStore(path).Load();

        Assert.Equal("auto", settings.Lang);
        Assert.Equal("auto", settings.Values.Perm);     // a weird permission value falls back to the default, not "add no argument"
        Assert.Equal("new", settings.Values.Session);
        Assert.Equal("default", settings.Values.Effort);
    }

    [Fact]
    public void Known_language_code_is_kept()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """{ "lang": "zht" }""");

        Assert.Equal("zht", new ConfigStore(path).Load().Lang);
    }

    [Fact]
    public void Unknown_keys_are_preserved_on_save()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """
        { "lang": "ko", "내가_나중에_추가할_설정": { "깊은": [1, 2, 3] }, "theme": "dark" }
        """);

        var store = new ConfigStore(path);
        var settings = store.Load();
        settings.Folder = "/tmp/새폴더";
        Assert.True(store.Save(settings));

        var raw = store.LoadRaw();
        Assert.Equal("dark", (string?)raw["theme"]);
        Assert.Equal(3, raw["내가_나중에_추가할_설정"]!["깊은"]!.AsArray().Count);
        Assert.Equal("/tmp/새폴더", (string?)raw["folder"]);
        Assert.Equal("ko", (string?)raw["lang"]);
    }

    [Fact]
    public void Saved_values_read_back_unchanged()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        var store = new ConfigStore(path);

        var written = new IsleBarSettings
        {
            Lang = "ja",
            Folder = @"C:\Users\me\Desktop",
            Files = false,
            History = ["첫 질문", "두 번째 질문"],
            Quick = ["perm", "effort"],
            Models = ["opus", "fable"],
            ModelsChecked = 1_759_000_000,
            Values = new LaunchOptions { Session = "resume", Model = "opus", Effort = "max", Perm = "plan", Rc = true },
        };
        Assert.True(store.Save(written));

        var read = store.Load();
        Assert.Equal("ja", read.Lang);
        Assert.Equal(@"C:\Users\me\Desktop", read.Folder);
        Assert.False(read.Files);
        Assert.Equal(["첫 질문", "두 번째 질문"], read.History);
        Assert.Equal(["perm", "effort"], read.Quick);
        Assert.Equal(["opus", "fable"], read.Models);
        Assert.Equal(1_759_000_000, read.ModelsChecked);
        Assert.Equal("resume", read.Values.Session);
        Assert.Equal("opus", read.Values.Model);
        Assert.Equal("max", read.Values.Effort);
        Assert.Equal("plan", read.Values.Perm);
        Assert.True(read.Values.Rc);
    }

    [Fact]
    public void Korean_is_written_unescaped()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        var store = new ConfigStore(path);
        var settings = store.Load();
        settings.History = ["작업표시줄 검색창 만들어 줘"];
        Assert.True(store.Save(settings));

        Assert.Contains("작업표시줄 검색창 만들어 줘", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Migrates_legacy_flags_setting()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """{ "flags": ["--remote-control", "--resume"] }""");

        var store = new ConfigStore(path);
        var settings = store.Load();
        Assert.True(settings.Values.Rc);
        Assert.Equal("resume", settings.Values.Session);

        Assert.True(store.Save(settings));
        Assert.Null(store.LoadRaw()["flags"]);       // migrated, so removed
    }

    [Fact]
    public void Flags_are_ignored_when_values_exist()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """
        { "flags": ["--remote-control"], "values": { "rc": false, "session": "continue" } }
        """);

        var settings = new ConfigStore(path).Load();
        Assert.False(settings.Values.Rc);
        Assert.Equal("continue", settings.Values.Session);
    }

    [Fact]
    public void History_over_50_keeps_only_the_most_recent()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        var arr = new JsonArray();
        for (var i = 0; i < 70; i++)
        {
            arr.Add((JsonNode)$"질문 {i}");
        }

        File.WriteAllText(path, new JsonObject { ["history"] = arr }.ToJsonString());

        var history = new ConfigStore(path).Load().History;
        Assert.Equal(50, history.Count);
        Assert.Equal("질문 20", history[0]);
        Assert.Equal("질문 69", history[^1]);
    }

    [Fact]
    public void Concurrent_saves_from_many_threads_do_not_corrupt_the_file()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        var store = new ConfigStore(path);

        // Each thread adds a key with its own name — all must survive at the end (meaning read-modify-write never overlapped)
        const int threads = 8;
        const int rounds = 25;
        var failures = 0;
        Parallel.For(0, threads, t =>
        {
            for (var r = 0; r < rounds; r++)
            {
                var ok = store.Update(raw =>
                {
                    raw[$"thread{t}"] = r;
                    return true;
                });
                if (!ok)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        Assert.Equal(0, failures);
        var raw = store.LoadRaw();
        for (var t = 0; t < threads; t++)
        {
            Assert.Equal(rounds - 1, (int?)raw[$"thread{t}"]);
        }
    }

    [Fact]
    public async Task Concurrent_save_and_load_never_sees_broken_settings()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        var store = new ConfigStore(path);
        var seed = store.Load();
        seed.Lang = "ko";
        Assert.True(store.Save(seed));

        var stop = false;
        var writer = Task.Run(() =>
        {
            var n = 0;
            while (!Volatile.Read(ref stop))
            {
                store.Update(s =>
                {
                    s.History = [$"질문 {n++}"];
                    return true;
                });
            }
        });

        // Keep reading while writing. Thanks to the atomic swap "ko" must always be visible (falling back to the default "auto" means a half-written file was read).
        var reads = 0;
        for (var i = 0; i < 400; i++)
        {
            Assert.Equal("ko", store.Load().Lang);
            reads++;
        }

        Volatile.Write(ref stop, true);
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(400, reads);
    }

    [Fact]
    public void No_temp_files_remain_after_saving()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        var store = new ConfigStore(path);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(store.Save(store.Load()));
        }

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp-*"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.lock"));   // DeleteOnClose
    }

    [Fact]
    public void Update_returning_false_does_not_write()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        var store = new ConfigStore(path);

        Assert.True(store.Update((IsleBarSettings s) =>
        {
            s.Lang = "de";
            return false;
        }));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Creates_missing_folder_when_saving()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "깊은", "폴더", "islebar.json");
        var store = new ConfigStore(path);
        Assert.True(store.Save(store.Load()));
        Assert.True(File.Exists(path));
    }
}

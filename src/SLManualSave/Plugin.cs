using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GameCore.HotUpdate;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using ObservableCollections;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Vuplex.WebView;

namespace SLManualSave;

[BepInPlugin("survivallog.manualsave", "SL Manual Save", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = base.Log;
        Logger.LogInfo("SLManualSave v1.0.0 loading");

        ClassInjector.RegisterTypeInIl2Cpp<HotkeyMono>();

        var go = new GameObject("SLManualSave_Hotkey");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<HotkeyMono>();

        try
        {
            var harmony = new Harmony("survivallog.manualsave");
            harmony.PatchAll(typeof(WebUiBridgePatch));
            harmony.PatchAll(typeof(SaveListNamePatch));
            harmony.PatchAll(typeof(SaveListDumpPatch));
            Logger.LogInfo("webui bridge + save list name patches applied");
        }
        catch (Exception ex)
        {
            Logger.LogError("failed to patch WebUI bridge: " + ex);
        }

        Logger.LogInfo("ready: ESC menu -> 保存游戏, or F5 (default name), F9 = dump");
    }
}

/// <summary>Receives page actions from the WebUI bridge: typeId \x1E pageId \x1E action \x1E json.</summary>
[HarmonyPatch]
public static class WebUiBridgePatch
{
    private static readonly Regex NameRegex =
        new Regex("\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    [HarmonyPatch(typeof(WebUILayer), "OnMessageFromJS")]
    [HarmonyPrefix]
    public static bool Prefix(WebUILayer __instance, Il2CppSystem.Object sender, EventArgs<string> eventArgs)
    {
        try
        {
            var message = eventArgs?.Value;
            if (string.IsNullOrEmpty(message))
            {
                return true;
            }

            if (message.IndexOf("SL_INPUT_FOCUS", StringComparison.Ordinal) >= 0)
            {
                var focused = Regex.IsMatch(message, "\"focused\"\\s*:\\s*true");
                SetTextInputMode(__instance, focused);
                return false;
            }

            if (message.IndexOf("SAVE_MANUAL", StringComparison.Ordinal) < 0)
            {
                return true;
            }

            string displayName = null;
            var parts = message.Split('\u001E');
            if (parts.Length >= 4)
            {
                var match = NameRegex.Match(parts[3]);
                if (match.Success)
                {
                    displayName = Regex.Unescape(match.Groups[1].Value);
                }
            }

            Plugin.Logger?.LogInfo($"menu save requested, name='{displayName}'");
            SetTextInputMode(__instance, false);
            ManualSave.Checkpoint(displayName);
            return false; // swallow: the game must not try to route this action
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("webui patch handler failed: " + ex);
            return true;
        }
    }

    /// <summary>Route keyboard / IME input into the webview while the page has a focused text field.</summary>
    private static void SetTextInputMode(WebUILayer layer, bool focused)
    {
        try
        {
            layer?.SetKeyboardEnabled(focused);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("SetKeyboardEnabled failed: " + ex.Message);
        }

        try
        {
            GameCore.Scripts.ImeGuard.SetEnabled(focused);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("ImeGuard.SetEnabled failed: " + ex.Message);
        }

        Plugin.Logger?.LogInfo($"text input mode = {focused}");
    }

    /// <summary>Safety net: never leave the webview holding keyboard focus after the menu page goes away.</summary>
    [HarmonyPatch(typeof(WebUILayer), "HidePage")]
    [HarmonyPostfix]
    public static void AfterHidePage(WebUILayer __instance, string pageId)
    {
        if (pageId == "OutSetting") SetTextInputMode(__instance, false);
    }

    [HarmonyPatch(typeof(WebUILayer), "RemovePage")]
    [HarmonyPostfix]
    public static void AfterRemovePage(WebUILayer __instance, string pageId)
    {
        if (pageId == "OutSetting") SetTextInputMode(__instance, false);
    }
}

/// <summary>Sidecar store that remembers user-given save names, keyed by save file name.</summary>
internal static class NameStore
{
    private static readonly object Gate = new object();
    private static readonly string Dir = Path.Combine(BepInEx.Paths.PluginPath, "SLManualSave");
    private static readonly string NameFile = Path.Combine(Dir, "save_names.txt");
    private static System.Collections.Generic.Dictionary<string, string> _map;

    public static string Get(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        lock (Gate)
        {
            EnsureLoaded();
            return _map.TryGetValue(fileName, out var name) ? name : null;
        }
    }

    public static void Set(string fileName, string name)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(name)) return;
        lock (Gate)
        {
            EnsureLoaded();
            _map[fileName] = name;
            Persist();
        }
    }

    private static void EnsureLoaded()
    {
        if (_map != null) return;
        _map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(NameFile)) return;
            foreach (var line in File.ReadAllLines(NameFile))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                try
                {
                    _map[line.Substring(0, tab)] =
                        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(tab + 1)));
                }
                catch
                {
                    // skip malformed line
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("NameStore load failed: " + ex.Message);
        }
    }

    private static void Persist()
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            var lines = new System.Collections.Generic.List<string>();
            foreach (var kv in _map)
            {
                lines.Add(kv.Key + "\t" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(kv.Value)));
            }
            File.WriteAllLines(NameFile, lines);
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("NameStore save failed: " + ex.Message);
        }
    }
}

/// <summary>Overrides the displayed role/name in the load-game list with the user-given save name.</summary>
[HarmonyPatch]
public static class SaveListNamePatch
{
    [HarmonyPatch(typeof(Reducer_Web_MainUI), "FillSaveInfoFromHistory")]
    [HarmonyPostfix]
    public static void PostfixFromHistory(Data_Web_Save data, HistoryChild item)
    {
        Apply(data, item?.FileName);
    }

    [HarmonyPatch(typeof(Reducer_Web_MainUI), "FillSaveInfo")]
    [HarmonyPostfix]
    public static void PostfixFromGameSave(Data_Web_Save data, GameSaveData gameSave)
    {
        Apply(data, gameSave?.FileName);
    }

    private static void Apply(Data_Web_Save data, string fileName)
    {
        try
        {
            var custom = NameStore.Get(fileName);
            if (string.IsNullOrEmpty(custom) || data == null) return;

            var prop = data.RoleText;
            if (prop != null)
            {
                prop.Value = custom;
            }

            // our copies carry no progress-time meta, so the row would show no date: fill it from the file name
            try
            {
                if (data.Info1 == null || string.IsNullOrEmpty(data.Info1.Value))
                {
                    var stamp = ParseStampFromFileName(fileName);
                    if (stamp.HasValue && data.Info1 != null)
                    {
                        data.Info1.Value = stamp.Value.ToString("MM/dd HH:mm");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("save list time fill failed: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("save list name override failed: " + ex.Message);
        }
    }

    internal static DateTime? ParseStampFromFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var m = Regex.Match(fileName, @"(\d{4})_(\d{2})_(\d{2})_(\d{2})_(\d{2})_(\d{2})");
        if (!m.Success) return null;
        try
        {
            return new DateTime(
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Diagnostics: dump the exact fields the load-game list shows.</summary>
[HarmonyPatch]
public static class SaveListDumpPatch
{
        [HarmonyPatch(typeof(WebUI_MainUI), "UpdateListView_Save")]
    [HarmonyPrefix]
    public static void BeforeUpdate(ObservableCollections.ObservableList<Data_Web_Save> sources)
    {
        try
        {
            if (sources == null) return;
            for (var i = 0; i < sources.Count; i++)
            {
                var d = sources[i];
                if (d == null || string.IsNullOrEmpty(d.FileName)) continue;
                if (NameStore.Get(d.FileName) == null) continue;
                if (d.Info1 == null || !string.IsNullOrEmpty(d.Info1.Value)) continue;

                var stamp = SaveListNamePatch.ParseStampFromFileName(d.FileName);
                if (stamp.HasValue) d.Info1.Value = stamp.Value.ToString("MM/dd HH:mm");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("save list time prefix failed: " + ex.Message);
        }
    }
[HarmonyPatch(typeof(WebUI_MainUI), "UpdateListView_Save")]
    [HarmonyPostfix]
    public static void AfterUpdate(WebUI_MainUI __instance)
    {
        try
        {
            var msg = __instance._SaveListMsg;
            var items = msg?.saveList;
            if (items == null) return;

            Plugin.Logger?.LogInfo($"[list] {items.Count} entries");
            for (var i = 0; i < items.Count && i < 12; i++)
            {
                var it = items[i];
                if (it == null) continue;
                Plugin.Logger?.LogInfo(
                    $"[list] #{i} idx={it.Index} role='{it.roleText}' time='{it.timeText}' turn='{it.turnText}' " +
                    $"diff='{it.diffText}' info1='{it.info1}' phase='{it.phaseText}' state='{it.stateText}'");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("save list dump failed: " + ex.Message);
        }
    }
}

/// <summary>Runtime driver: polls the hotkeys and draws a short on-screen toast.</summary>
public class HotkeyMono : MonoBehaviour
{
    public HotkeyMono(IntPtr ptr) : base(ptr) { }

    private static string _toastText;
    private static float _toastUntil;
    private static bool _guiErrorLogged;

    public static void Toast(string message)
    {
        _toastText = message;
        _toastUntil = Time.realtimeSinceStartup + 5f;
        Plugin.Logger?.LogInfo("[toast] " + message);
    }

    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                ManualSave.Checkpoint(null);
            }
            else if (Input.GetKeyDown(KeyCode.F9))
            {
                ManualSave.DumpState();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError("hotkey handler failed: " + ex);
        }
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_toastText) || Time.realtimeSinceStartup > _toastUntil)
        {
            return;
        }

        try
        {
            var rect = new Rect(Screen.width * 0.5f - 320f, Screen.height - 130f, 640f, 44f);
            GUI.Label(rect, _toastText);
        }
        catch (Exception ex)
        {
            if (!_guiErrorLogged)
            {
                _guiErrorLogged = true;
                Plugin.Logger?.LogError("toast draw failed: " + ex);
            }
        }
    }
}

internal static class ManualSave
{
    private static ManualLogSource Log => Plugin.Logger;

    public static void DumpState()
    {
        try
        {
            var mgr = GameSaveManager.Instance;
            if (mgr == null)
            {
                HotkeyMono.Toast("存档管理器未就绪");
                return;
            }

            Log.LogInfo("=== dump ===");
            Log.LogInfo("IsInBattle        : " + mgr.IsInBattle);
            Log.LogInfo("LastPlayFileName  : " + Safe(() => mgr.GetLastPlayFileName()));
            Log.LogInfo("LastPlaySavePath  : " + Safe(() => mgr.GetLastPlaySaveFilePath()));
            Log.LogInfo("HistorySavePath   : " + Safe(() => mgr.GetHistorySaveFilePath()));
            Log.LogInfo("HistoryCount      : " + Safe(() => mgr.GetHistoryCount().ToString()));
            Log.LogInfo("HistorySlotMax    : " + Safe(() => GameSaveManager.HistoryListMaxCount.ToString()));
            Log.LogInfo("GeneratedNewName  : " + Safe(() => mgr.GenerateNewSaveFileName()));

            var save = mgr.GetGameSaveData();
            if (save != null)
            {
                Log.LogInfo("GameSave.Name     : " + save.Name);
                Log.LogInfo("GameSave.FileName : " + save.FileName);
                Log.LogInfo("GameSave.Day      : " + (save.CurSave != null ? save.CurSave.Day.ToString() : "<null>"));
            }

            var history = mgr.GetHistoryList();
            if (history != null)
            {
                Log.LogInfo("history entries   : " + history.Count);
                for (var i = 0; i < history.Count; i++)
                {
                    var child = history[i];
                    if (child == null) continue;
                    Log.LogInfo($"  [{i}] {child.FileName} | name={child.Name} | day={child.Day} | maxDay={child.MaxDay} | finished={child.IsFinished}");
                }
            }

            HotkeyMono.Toast("存档信息已写入 BepInEx 日志");
        }
        catch (Exception ex)
        {
            Log.LogError("dump failed: " + ex);
            HotkeyMono.Toast("读取存档信息失败，见日志");
        }
    }

    public static void Checkpoint(string displayName)
    {
        string step = "init";
        try
        {
            var mgr = GameSaveManager.Instance;
            if (mgr == null)
            {
                HotkeyMono.Toast("存档管理器未就绪");
                return;
            }

            if (!mgr.IsInBattle)
            {
                HotkeyMono.Toast("不在游戏局内，无法存档");
                return;
            }

            step = "world safety check";
            try
            {
                if (!mgr.IsWorldSafeToPersist("SLManualSave"))
                {
                    HotkeyMono.Toast("当前世界仍未就绪（加载/切换中），已取消存档");
                    Log.LogWarning("checkpoint aborted: IsWorldSafeToPersist == false");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("IsWorldSafeToPersist check failed (continuing): " + ex.Message);
            }

            step = "sub-game check";
            var preSave = mgr.GetGameSaveData();
            if (preSave != null && preSave.IsSubGame)
            {
                HotkeyMono.Toast("当前在探索副本/子局内，暂不支持手动存档");
                Log.LogWarning("checkpoint aborted: IsSubGame == true");
                return;
            }

            step = "flush current save";
            var curPath = mgr.GetLastPlaySaveFilePath();
            var stampBefore = File.Exists(curPath) ? File.GetLastWriteTimeUtc(curPath) : DateTime.MinValue;
            mgr.SaveGame();

            step = "wait for disk write";
            var flushed = false;
            for (var i = 0; i < 40; i++)
            {
                if (!File.Exists(curPath)) break;
                if (File.GetLastWriteTimeUtc(curPath) != stampBefore) { flushed = true; break; }
                System.Threading.Thread.Sleep(50);
            }
            if (!flushed)
            {
                Log.LogWarning("save file mtime did not change within 2s, copying the current file anyway");
            }

            step = "read current file";
            var curFile = mgr.GetLastPlayFileName();
            if (string.IsNullOrEmpty(curFile) || string.IsNullOrEmpty(curPath) || !File.Exists(curPath))
            {
                HotkeyMono.Toast("找不到当前存档文件，已放弃");
                Log.LogWarning($"checkpoint aborted: curFile='{curFile}' curPath='{curPath}'");
                return;
            }

            step = "check slot capacity";
            var capacity = SafeInt(() => GameSaveManager.HistoryListMaxCount);
            var used = SafeInt(() => mgr.GetHistoryCount());
            if (capacity > 0 && used >= capacity)
            {
                HotkeyMono.Toast($"存档位已满（{used}/{capacity}），请先在主菜单删掉一个旧档");
                return;
            }

            step = "generate new name";
            var dir = Path.GetDirectoryName(curPath);
            string newFile = null;
            string newPath = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var candidate = mgr.GenerateNewSaveFileName();
                if (string.IsNullOrEmpty(candidate)) break;
                var path = Path.Combine(dir, candidate);
                if (!File.Exists(path))
                {
                    newFile = candidate;
                    newPath = path;
                    break;
                }
                // same-second collision: wait for the clock to tick and try again
                System.Threading.Thread.Sleep(1100);
            }

            if (newFile == null)
            {
                HotkeyMono.Toast("无法生成新的存档文件名，已放弃");
                return;
            }

            step = "copy file (temp)";
            var tmpPath = newPath + ".tmp";
            File.Copy(curPath, tmpPath, true);

            step = "patch internal file name";
            var patched = PatchInternalFileName(tmpPath, curFile, newFile);
            Log.LogInfo($"patched internal name occurrences: {patched}");

            step = "commit copy";
            if (File.Exists(newPath)) File.Delete(newPath);
            File.Move(tmpPath, newPath);
            try
            {
                File.SetLastWriteTime(newPath, DateTime.Now);
                if (File.Exists(curPath + ".bak")) File.Copy(curPath + ".bak", newPath + ".bak", true);
            }
            catch (Exception ex)
            {
                Log.LogWarning("optional timestamp/.bak step failed (ignored): " + ex.Message);
            }

            step = "add history entry";
            var save = mgr.GetGameSaveData();
            var name = string.IsNullOrEmpty(displayName) ? null : displayName.Trim();
            if (string.IsNullOrEmpty(name))
            {
                name = save != null && !string.IsNullOrEmpty(save.Name) ? save.Name : curFile;
            }
            mgr.AddHistoryEntry(newFile, name);

            step = "remember custom name";
            if (!string.IsNullOrEmpty(displayName))
            {
                NameStore.Set(newFile, displayName.Trim());
            }

            step = "restore last play pointer";
            if (mgr.GetLastPlayFileName() != curFile)
            {
                mgr.SetLastPlayFileName(curFile);
            }

            step = "persist history";
            mgr.PersistHistoryData();

            var day = save != null && save.CurSave != null ? save.CurSave.Day : -1;
            Log.LogInfo($"checkpoint ok: cur={curFile} -> new={newFile} day={day} name={name}");
            HotkeyMono.Toast($"已保存「{name}」（Day {day}）");
        }
        catch (Exception ex)
        {
            Log.LogError($"checkpoint failed at step '{step}': {ex}");
            HotkeyMono.Toast($"存档失败（{step}），详见 BepInEx 日志");
        }
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "<null>"; }
        catch (Exception ex) { return "<error: " + ex.GetType().Name + ">"; }
    }

    private static int SafeInt(Func<int> f)
    {
        try { return f(); }
        catch { return -1; }
    }

    /// <summary>
    /// Rewrite the save's own "FileName" field inside the copied file so the new slot is self-consistent.
    /// Both names share the same length (Save_yyyy_MM_dd_HH_mm_ss.bytes), so this is a pure in-place replace.
    /// </summary>
    private static int PatchInternalFileName(string path, string oldName, string newName)
    {
        if (oldName.Length != newName.Length) return 0;

        var data = File.ReadAllBytes(path);
        var hits = 0;

        hits += ReplaceAll(data, System.Text.Encoding.ASCII.GetBytes(oldName), System.Text.Encoding.ASCII.GetBytes(newName));
        hits += ReplaceAll(data, System.Text.Encoding.Unicode.GetBytes(oldName), System.Text.Encoding.Unicode.GetBytes(newName));
        hits += ReplaceAll(data, System.Text.Encoding.BigEndianUnicode.GetBytes(oldName), System.Text.Encoding.BigEndianUnicode.GetBytes(newName));

        if (hits > 0) File.WriteAllBytes(path, data);
        return hits;
    }

    private static int ReplaceAll(byte[] data, byte[] needle, byte[] replacement)
    {
        if (needle.Length == 0 || needle.Length != replacement.Length) return 0;

        var hits = 0;
        var limit = data.Length - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j]) { match = false; break; }
            }
            if (!match) continue;

            for (var j = 0; j < replacement.Length; j++) data[i + j] = replacement[j];
            hits++;
            i += needle.Length - 1;
        }
        return hits;
    }
}

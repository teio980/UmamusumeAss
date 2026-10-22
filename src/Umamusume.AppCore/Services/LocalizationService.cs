using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Umamusume.AppCore.Services;

public interface ILocalizationService
{
    string CurrentLanguage { get; set; }
    event Action? LanguageChanged;
    string GetString(string key);
    string this[string key] => GetString(key);
}

public sealed class LocalizationService : ILocalizationService
{
    private string _currentLanguage = "zh-CN";

    public event Action? LanguageChanged;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                LanguageChanged?.Invoke();
            }
        }
    }

    private static readonly Dictionary<string, string> ZhStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AppTitle"] = "赛马娘小助手 (UmamusumeAss)",
        ["NavOverview"] = "总览",
        ["NavConnection"] = "连接设备",
        ["NavScreenshot"] = "截图 & OCR",
        ["NavTasks"] = "自动化任务",
        ["NavLogs"] = "运行日志",
        ["NavSettings"] = "系统设置",
        ["Connect"] = "连接",
        ["Disconnect"] = "断开",
        ["Connecting"] = "连接中...",
        ["Connected"] = "已连接",
        ["Disconnected"] = "未连接",
        ["AdbPath"] = "ADB 路径",
        ["DeviceAddress"] = "设备地址 (IP:Port)",
        ["SelectConfig"] = "配置预设",
        ["RefreshDevices"] = "刷新设备",
        ["CaptureScreen"] = "截取屏幕",
        ["RunOcr"] = "文字识别 (OCR)",
        ["StartGame"] = "启动游戏",
        ["StopTasks"] = "停止任务",
        ["StartGameTask"] = "启动游戏并识别首页",
        ["RecognizingHome"] = "正在识别首页...",
        ["HomeFound"] = "成功识别到游戏首页！",
        ["ClearLogs"] = "清空日志",
        ["Language"] = "界面语言",
        ["Theme"] = "外观主题",
        ["SaveSettings"] = "保存配置",
        ["DeviceInfo"] = "设备信息",
        ["Resolution"] = "屏幕分辨率",
        ["Serial"] = "设备序列号",
        ["OcrResults"] = "OCR 识别结果",
        ["Confidence"] = "置信度",
        ["Coordinates"] = "坐标",
        ["TaskStatus"] = "任务状态",
        ["Idle"] = "空闲",
        ["Running"] = "运行中",
        ["Completed"] = "已完成",
        ["Failed"] = "失败",
        ["BlueStacksAirDetected"] = "已预选本机 BlueStacks Air (127.0.0.1:5555)",
    };

    private static readonly Dictionary<string, string> EnStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AppTitle"] = "Umamusume Assistant",
        ["NavOverview"] = "Overview",
        ["NavConnection"] = "Connection",
        ["NavScreenshot"] = "Screenshot & OCR",
        ["NavTasks"] = "Tasks",
        ["NavLogs"] = "Logs",
        ["NavSettings"] = "Settings",
        ["Connect"] = "Connect",
        ["Disconnect"] = "Disconnect",
        ["Connecting"] = "Connecting...",
        ["Connected"] = "Connected",
        ["Disconnected"] = "Disconnected",
        ["AdbPath"] = "ADB Path",
        ["DeviceAddress"] = "Device Address (IP:Port)",
        ["SelectConfig"] = "Configuration Preset",
        ["RefreshDevices"] = "Refresh Devices",
        ["CaptureScreen"] = "Capture Screen",
        ["RunOcr"] = "Run OCR",
        ["StartGame"] = "Start Game",
        ["StopTasks"] = "Stop Tasks",
        ["StartGameTask"] = "Start Game & Recognize Home",
        ["RecognizingHome"] = "Recognizing Home Screen...",
        ["HomeFound"] = "Game home screen recognized successfully!",
        ["ClearLogs"] = "Clear Logs",
        ["Language"] = "Language",
        ["Theme"] = "Theme",
        ["SaveSettings"] = "Save Settings",
        ["DeviceInfo"] = "Device Info",
        ["Resolution"] = "Resolution",
        ["Serial"] = "Serial Number",
        ["OcrResults"] = "OCR Results",
        ["Confidence"] = "Confidence",
        ["Coordinates"] = "Coordinates",
        ["TaskStatus"] = "Task Status",
        ["Idle"] = "Idle",
        ["Running"] = "Running",
        ["Completed"] = "Completed",
        ["Failed"] = "Failed",
        ["BlueStacksAirDetected"] = "Pre-selected host BlueStacks Air (127.0.0.1:5555)",
    };

    private static readonly Lazy<Dictionary<string, string>> TaskLogZh = new(() => ReadTaskLogStrings("zh-CN"));
    private static readonly Lazy<Dictionary<string, string>> TaskLogEn = new(() => ReadTaskLogStrings("en-US"));

    private static Dictionary<string, string> ReadTaskLogStrings(string language)
    {
        using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream($"TaskLog.Strings.{language}.xaml")!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(stream).Root!.Elements()
            .Where(element => element.Attribute(x + "Key")?.Value is { } key
                && (key.StartsWith("HachimiTaskLog_", StringComparison.Ordinal)
                    || key.StartsWith("GrassSmartTraining", StringComparison.Ordinal)
                    || key.StartsWith("GrassNormalTraining", StringComparison.Ordinal)))
            .ToDictionary(element => element.Attribute(x + "Key")!.Value, element => element.Value, StringComparer.Ordinal);
    }

    public string GetString(string key)
    {
        var dict = _currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? ZhStrings : EnStrings;
        if (dict.TryGetValue(key, out var val)) return val;
        if (EnStrings.TryGetValue(key, out val)) return val;
        var taskLog = _currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? TaskLogZh : TaskLogEn;
        if (taskLog.Value.TryGetValue(key, out val)) return val;
        return key;
    }
}

using System;
using System.IO;

namespace TReader.Services;

/// <summary>
/// 应用数据目录解析。
/// 优先使用可执行文件旁的 data 目录（便携模式），无法写入时回退到 %LocalAppData%\TReader。
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// 当前使用的数据目录。
    /// </summary>
    public static string DataDirectory { get; }

    /// <summary>
    /// 是否处于便携模式。
    /// </summary>
    public static bool IsPortableMode { get; }

    static AppPaths()
    {
        var portableDataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        if (CanUseDirectory(portableDataDirectory))
        {
            IsPortableMode = true;
            DataDirectory = portableDataDirectory;
            return;
        }

        IsPortableMode = false;
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TReader");
        Directory.CreateDirectory(DataDirectory);
    }

    private static bool CanUseDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var testFile = Path.Combine(directory, ".write-test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

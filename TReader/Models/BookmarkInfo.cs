using System;
using System.Text.Json.Serialization;

namespace TReader.Models;

/// <summary>
/// 阅读书签
/// </summary>
public class BookmarkInfo
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 书签标题
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 所在章节
    /// </summary>
    public string ChapterTitle { get; set; } = string.Empty;

    /// <summary>
    /// 原始文本行号（从0开始）
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// 界面显示行号（从1开始）
    /// </summary>
    [JsonIgnore]
    public int DisplayLineNumber => LineNumber + 1;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public override string ToString() => Title;
}

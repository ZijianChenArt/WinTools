using System;
using System.Text;

namespace WinTools.Services;

/// <summary>
/// 汉字转拼音：为「悬浮搜索」提供「全拼」和「首字母」两把检索键，
/// 让「百度网盘」既能用 <c>baiduwangpan</c> 也能用 <c>bdwp</c> 搜到。
/// </summary>
/// <remarks>查找表在自动生成的 <c>PinyinData.cs</c> 里，本文件只放取词与拆分逻辑。</remarks>
internal static partial class Pinyin
{
    /// <summary>取单字读音；不在表内（非汉字或生僻码位）时返回空。</summary>
    private static ReadOnlySpan<char> Syllable(char c)
    {
        if (c < FirstCodePoint || c > LastCodePoint) return default;
        return Table.AsSpan((c - FirstCodePoint) * RecordWidth, RecordWidth).TrimEnd(' ');
    }

    /// <summary>一个名称拆出来的三把检索键，都只含小写字母与数字。</summary>
    /// <param name="Full">全拼："百度网盘" → baiduwangpan，"QQ音乐" → qqyinle。</param>
    /// <param name="Initials">首字母："百度网盘" → bdwp，"NVIDIA Control Panel" → ncp。</param>
    /// <param name="Acronym">
    /// 把连续大写当成各自独立的词再取一次首字母："QQ音乐" → qqyl。
    /// 与 <paramref name="Initials"/> 相同时为空串——两者只在名称含全大写段落时才有区别。
    /// </param>
    public readonly record struct Keys(string Full, string Initials, string Acronym);

    /// <summary>把名称拆成全拼与两种首字母缩写。</summary>
    /// <remarks>
    /// 汉字按单字注音，每个字都算一个词（所以「百度网盘」的首字母是 bdwp）；
    /// 拉丁字母按分隔符断词，并识别驼峰与缩写边界：
    /// "Visual Studio Code" → vsc，"OneNote" → on，"HTMLParser" → hp。
    /// </remarks>
    public static Keys Build(string text)
    {
        if (string.IsNullOrEmpty(text)) return new Keys("", "", "");

        var full = new StringBuilder(text.Length * 3);
        var initials = new StringBuilder(text.Length);
        var acronym = new StringBuilder(text.Length);
        var newWord = true;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            var syllable = Syllable(c);
            if (!syllable.IsEmpty)
            {
                full.Append(syllable);
                initials.Append(syllable[0]);
                acronym.Append(syllable[0]);
                newWord = true;
                continue;
            }

            if (!char.IsLetterOrDigit(c))
            {
                newWord = true;
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            full.Append(lower);
            if (newWord || IsWordBoundary(text, i)) initials.Append(lower);
            if (newWord || char.IsUpper(c)) acronym.Append(lower);
            newWord = false;
        }

        var initialsText = initials.ToString();
        var acronymText = acronym.ToString();
        return new Keys(
            full.ToString(),
            initialsText,
            string.Equals(acronymText, initialsText, StringComparison.Ordinal) ? "" : acronymText);
    }

    /// <summary>大写字母是否开启了新的一个词（驼峰边界，或一段全大写缩写的末字）。</summary>
    private static bool IsWordBoundary(string text, int index)
    {
        var c = text[index];
        if (!char.IsUpper(c) || index == 0) return false;

        var previous = text[index - 1];
        // "oneNote" / "app2Web"：小写或数字后面的大写字母。
        if (char.IsLower(previous) || char.IsDigit(previous)) return true;
        // "HTMLParser"：全大写段落的最后一个字母才是下一个词的词首。
        return char.IsUpper(previous) && index + 1 < text.Length && char.IsLower(text[index + 1]);
    }
}

using System.IO;

/// <summary>
/// Source reads for clinical checks, normalized to LF. The checks pin multi-line literals
/// with \n; git materializes working files as CRLF on Windows (eol=crlf), which otherwise
/// fails every such assertion the first time a merge or checkout rewrites the file.
/// </summary>
internal static class SourceText
{
    public static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");
}

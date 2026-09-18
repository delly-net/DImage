using System.Reflection;

namespace DImage.Api.Imaging.Glyphs;

/// <summary>
/// 程序集内嵌字库资源的读取。
/// </summary>
/// <remarks>
/// <para>
/// <b>资源来自程序集清单,不读文件系统</b>:字库必须随部署产物走,缺失时应当是
/// 「启动即失败」而不是「运行时按当前工作目录找文件」。这也让 <c>Imaging/</c> 保持
/// 「不读配置、不读环境」的既有约束 —— 它连 <c>AppContext.BaseDirectory</c> 都不碰。
/// </para>
/// <para>
/// <b>按后缀匹配而非拼死清单名。</b>内嵌资源名 = <c>{RootNamespace}.{文件夹路径}.{文件名}</c>,
/// 而 <c>RootNamespace</c> 与 <c>AssemblyName</c> 在本项目里<b>不同</b>
/// (前者 <c>DImage.Api</c>、后者 <c>dimage</c>)。拼死名字会在有人调整
/// <c>RootNamespace</c> 或挪动文件时静默返回 <c>null</c>,再由下游抛一个「流为空」的
/// 无关异常。后缀匹配把这种失败收敛到本方法内,并给出「实际有哪些资源」的可读消息。
/// </para>
/// </remarks>
internal static class EmbeddedResources
{
    /// <summary>
    /// 按文件名后缀打开一个内嵌资源流。
    /// </summary>
    /// <param name="fileName">文件名(如 <c>latin-rowmans.jhf</c>),按 <c>.{fileName}</c> 后缀匹配。</param>
    /// <returns>只读流,由调用方释放。</returns>
    /// <exception cref="InvalidOperationException">程序集内没有该资源。</exception>
    internal static Stream Open(string fileName)
    {
        Assembly assembly = typeof(EmbeddedResources).Assembly;
        string[] names = assembly.GetManifestResourceNames();
        string suffix = "." + fileName;

        foreach (string name in names)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException(
                        $"内嵌资源 {name} 无法打开为流(程序集 {assembly.FullName})。");
            }
        }

        throw new InvalidOperationException(
            $"程序集内没有内嵌资源 *{suffix};实际资源清单:{string.Join("、", names)}。"
            + "请确认 DImage.Api.csproj 的 <EmbeddedResource> 项与文件名一致。");
    }

    /// <summary>把一个内嵌资源整体读成字节数组。</summary>
    /// <param name="fileName">文件名,见 <see cref="Open"/>。</param>
    internal static byte[] ReadAllBytes(string fileName)
    {
        using Stream stream = Open(fileName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

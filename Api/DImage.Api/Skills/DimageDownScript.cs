namespace DImage.Api.Skills;

/// <summary>
/// 随 <c>dimage-on</c> 技能一并分发的 <c>dimage-down.py</c> 脚本正文(唯一事实源)。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是 C# 常量而不是内嵌资源</b>:本脚本<b>不含任何插值</b>(服务地址与 TOKEN 由脚本在运行期
/// 从项目根 <c>.mcp.json</c> 读取),即一段纯常量文本。做成 <c>EmbeddedResource</c> 只会额外引入
/// MSBuild 的资源命名规则(参见 <c>DImage.Api.csproj</c> 中「中间扩展名被当作语言代码剥离」的实测记录),
/// 而这里没有任何需要外部维护或按目录结构定位的东西。既有两个 PowerShell 脚本同样以原始字符串常量承载,
/// 落点独立成文件只是避免 <c>SkillService</c> 继续膨胀。
/// </para>
/// <para>
/// <b>换行符不由本文件决定</b>:原始字符串字面量的行尾<b>随源文件</b>,而 <c>SkillService</c> 分发前一律经
/// <c>ToLf</c> 归一 —— 归一动作因此只有一处实现,不依赖「源文件恰好是 LF」这一脆弱前提。
/// </para>
/// </remarks>
public static class DimageDownScript
{
    /// <summary>脚本文件名(落盘名,同时是文件分发接口 <c>{file}</c> 段的查询值)。</summary>
    public const string FileName = "dimage-down.py";

    /// <summary>脚本正文;分发方负责做 LF 归一。</summary>
    /// <remarks>
    /// <b>末尾那个 <c>\n</c> 必须显式补上</b>:原始字符串字面量<b>不含</b>闭合定界符前的最后一个换行,
    /// 若直接返回 <see cref="Raw"/>,落盘的 <c>.py</c> 末行就没有行尾 —— 文本文件缺尾换行会让
    /// 后续 diff 显示「\ No newline at end of file」,而这类噪音一旦进了版本库就再难清掉。
    /// </remarks>
    public static string Content => Raw + "\n";

    /// <remarks>
    /// 定界符刻意用<b>四个</b>双引号:正文里的 Python 文档字符串是三个双引号,用三个会把内容提前截断。
    /// 前缀不加 <c>$</c>(脚本正文无插值),故 Python 的字面量花括号无需转义。
    /// </remarks>
    private const string Raw = """"
        #!/usr/bin/env python3
        """把小D图像(DImage)服务内存中的图像按 Id 下载到本地。

        用法:
            python dimage-down.py <图像id> [目标地址]

        对接 MCP 工具 image_download_png:服务端把内存图像编码为 PNG 后以图像内容块返回,
        本脚本直接解码落盘,因此**不需要把 base64 搬进对话上下文**。

        服务地址与 TOKEN 取自项目根 .mcp.json 的 mcpServers.dimage 条目,请先用 MCP 安装脚本
        装好接入配置:`irm <服务地址>/mcp/install | iex`。仅使用 Python 标准库,无需安装依赖。

        退出码:0 成功 / 1 用法或配置错误 / 2 运行期失败(服务端报错、网络不通、写盘失败)。
        """

        import argparse
        import base64
        import json
        import os
        import sys
        import urllib.error
        import urllib.request

        SERVER_KEY = "dimage"
        TOOL_NAME = "image_download_png"
        CONFIG_NAME = ".mcp.json"


        class ConfigError(Exception):
            """用法或配置有误:重跑多少次都不会变好,须先改配置。"""


        class DownloadError(Exception):
            """运行期失败:服务端报错、网络不通或写盘失败。"""


        def harden_stdout():
            """Windows 控制台默认 GBK,中文回执会被吃成乱码;能改则改成 UTF-8。"""
            for stream in (sys.stdout, sys.stderr):
                try:
                    stream.reconfigure(encoding="utf-8")
                except (AttributeError, ValueError):
                    pass


        def read_entry(path):
            """读 .mcp.json 并取出 dimage 条目;文件非法或无该条目时返回 None。"""
            try:
                with open(path, "r", encoding="utf-8") as handle:
                    config = json.load(handle)
            except (OSError, ValueError):
                return None

            servers = config.get("mcpServers") if isinstance(config, dict) else None
            entry = servers.get(SERVER_KEY) if isinstance(servers, dict) else None
            return entry if isinstance(entry, dict) else None


        def locate(start):
            """自 start 逐级向上查找含 mcpServers.dimage 的 .mcp.json。

            返回 (配置路径, 条目, 途经的 .mcp.json 列表);条目为 None 表示没找到可用配置,
            途经列表用于给出「文件在、但没配 dimage」这类可操作的提示。
            """
            seen = []
            current = os.path.abspath(start)

            while True:
                candidate = os.path.join(current, CONFIG_NAME)
                if os.path.isfile(candidate):
                    if candidate not in seen:
                        seen.append(candidate)
                    entry = read_entry(candidate)
                    if entry is not None:
                        return candidate, entry, seen

                parent = os.path.dirname(current)
                if parent == current:
                    return None, None, seen
                current = parent


        def resolve_target(target, image_id):
            """自适应解析落盘路径:目录 → {id}.png;否则按文件路径。

            父目录不存在时直接拒绝(不静默创建目录)—— 打错一个字母就悄悄多出一棵目录树,
            比报错难排查得多。
            """
            path = os.path.abspath(target)
            if os.path.isdir(path) or target.endswith(("/", "\\")):
                path = os.path.join(path, image_id + ".png")

            parent = os.path.dirname(path)
            if not os.path.isdir(parent):
                raise ConfigError(
                    "目标地址的父目录不存在:%s(本脚本不自动创建目录)" % parent)

            return path


        def call_tool(url, authorization, image_id, timeout):
            """POST 一次 MCP tools/call;端点无状态,无须 initialize 握手。"""
            payload = {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "tools/call",
                "params": {"name": TOOL_NAME, "arguments": {"id": image_id}},
            }
            request = urllib.request.Request(
                url,
                data=json.dumps(payload).encode("utf-8"),
                headers={
                    "Authorization": authorization,
                    "Content-Type": "application/json",
                    # Streamable HTTP 允许服务端回 SSE,不声明接受就会在部分实现上被拒
                    "Accept": "application/json, text/event-stream",
                },
                method="POST",
            )

            try:
                with urllib.request.urlopen(request, timeout=timeout) as response:
                    return response.read().decode("utf-8", "replace")
            except urllib.error.HTTPError as error:
                detail = error.read().decode("utf-8", "replace").strip()
                raise DownloadError("HTTP %s %s" % (error.code, detail[:300] or "(无响应体)"))
            except urllib.error.URLError as error:
                raise DownloadError("无法连接 %s:%s" % (url, error.reason))


        def parse_body(text):
            """解析响应体:既可能是 application/json,也可能是 SSE 的 data: 行。"""
            body = text.strip()
            if not body:
                raise DownloadError("响应体为空")
            if body.startswith("{"):
                return json.loads(body)

            for line in body.splitlines():
                line = line.strip()
                if line.startswith("data:"):
                    return json.loads(line[5:].strip())

            raise DownloadError("响应既不是 JSON 也不是 SSE:%s" % body[:300])


        def describe_error(blocks):
            """从失败结果的内容块里取出服务端的 code 与 message。"""
            for block in blocks:
                if not isinstance(block, dict) or block.get("type") != "text":
                    continue

                text = block.get("text") or ""
                try:
                    detail = json.loads(text)
                except ValueError:
                    return text[:300]

                if isinstance(detail, dict):
                    return "%s:%s" % (detail.get("code", "error"), detail.get("message", ""))
                return text[:300]

            return "服务端返回了失败结果,但没有给出原因"


        def extract_png(document):
            """取出 PNG 字节;服务端报错或没有图像块时抛 DownloadError。"""
            if "error" in document:
                error = document["error"] or {}
                raise DownloadError("JSON-RPC 错误 %s:%s" % (
                    error.get("code"), error.get("message")))

            result = document.get("result") or {}
            blocks = result.get("content") or []

            if result.get("isError"):
                raise DownloadError(describe_error(blocks))

            for block in blocks:
                if isinstance(block, dict) and block.get("type") == "image" and block.get("data"):
                    return base64.b64decode(block["data"])

            raise DownloadError("响应中没有图像内容块:%s" % str(blocks)[:300])


        def write_atomically(path, data):
            """先写同目录临时文件再替换:失败时不会留下半个目标文件。"""
            temporary = path + ".part"
            try:
                with open(temporary, "wb") as handle:
                    handle.write(data)
                os.replace(temporary, path)
            except OSError:
                if os.path.exists(temporary):
                    try:
                        os.remove(temporary)
                    except OSError:
                        pass
                raise


        def build_parser():
            parser = argparse.ArgumentParser(
                prog="dimage-down.py",
                description="把 DImage 服务内存中的图像按 Id 下载到本地(对接 MCP 工具 image_download_png)。",
                epilog="服务地址与 TOKEN 取自项目根 .mcp.json 的 mcpServers.dimage 条目;"
                       "目标地址已有的同名文件会被覆盖。",
            )
            parser.add_argument("id", help="图像 id(image_create / image_upload 返回的 32 位十六进制串)")
            parser.add_argument(
                "target",
                nargs="?",
                default=".",
                help="目标地址:已存在的目录(自动命名为 <id>.png)或文件路径;省略时写入当前目录",
            )
            parser.add_argument("--timeout", type=float, default=60.0, help="单次请求超时秒数,默认 60")
            return parser


        def run(options):
            # 先看当前工作目录,再看脚本所在目录:脚本装在 .claude/skills/dimage-on/ 下,
            # 用户在项目任意子目录里调用都应该能找到项目根的接入配置
            config_path, entry, seen = locate(os.getcwd())
            if entry is None:
                script_dir = os.path.dirname(os.path.abspath(__file__))
                config_path, entry, from_script = locate(script_dir)
                seen = seen + [path for path in from_script if path not in seen]

            if entry is None:
                hint = ""
                if seen:
                    hint = "\n已找到 %s,但它没有 mcpServers.%s 条目。" % (seen[0], SERVER_KEY)
                raise ConfigError(
                    "未找到 dimage 的服务接入配置(.mcp.json)。%s\n"
                    "请先在项目根执行:`irm <服务地址>/mcp/install | iex` 写入接入配置。" % hint)

            url = entry.get("url")
            if not isinstance(url, str) or not url.strip():
                raise ConfigError("%s 的 mcpServers.%s 缺少 url。" % (config_path, SERVER_KEY))

            headers = entry.get("headers")
            authorization = headers.get("Authorization") if isinstance(headers, dict) else None
            if not isinstance(authorization, str) or not authorization.strip():
                raise ConfigError(
                    "%s 的 mcpServers.%s 缺少 headers.Authorization。" % (config_path, SERVER_KEY))

            destination = resolve_target(options.target, options.id)

            document = parse_body(call_tool(
                url.strip(), authorization.strip(), options.id, options.timeout))
            png = extract_png(document)
            write_atomically(destination, png)

            print("已保存 %s(%d 字节)" % (destination, len(png)))
            return 0


        def main(argv):
            harden_stdout()
            options = build_parser().parse_args(argv)

            try:
                return run(options)
            except ConfigError as error:
                print("配置错误:%s" % error, file=sys.stderr)
                return 1
            except DownloadError as error:
                print("下载失败:%s" % error, file=sys.stderr)
                return 2
            except (OSError, ValueError) as error:
                print("下载失败:%s: %s" % (type(error).__name__, error), file=sys.stderr)
                return 2


        if __name__ == "__main__":
            sys.exit(main(sys.argv[1:]))
        """";
}

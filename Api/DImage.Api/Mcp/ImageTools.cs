using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DImage.Api.Imaging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DImage.Api.Mcp;

/// <summary>
/// 内存图像的五项 MCP 工具:创建、上传、释放、写入像素、导出 PNG。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型是唯一接触 <see cref="ImageBufferStore"/> 的对外入口</b>,且<b>不直接触碰
/// <see cref="ImageBuffer"/></b> —— 所有像素读写与编码都经注册表方法在条目锁内完成。
/// 工具层若自行 <c>TryGet</c> 出缓冲区再操作,就等于把并发写入的竞态从注册表里放了出来。
/// </para>
/// <para>
/// <b>错误一律以结构化结果返回,不靠抛异常</b>:前三项工具返回
/// <c>{ ok:false, code, message }</c>,第四项返回错误文本。异常经 SDK 包装后,
/// 调用方拿到的是一段难以结构化处理的堆栈文本 —— 而 MCP 工具的调用方是语言模型,
/// 它需要的是「哪个字段错了、期望什么、实际什么」,不是调用栈。
/// </para>
/// <para>
/// <b>上传与创建属同一能力域</b>:两者都是「在注册表里新建一张图并返回 Id」,共用同一套
/// Id 契约与结果形状(<see cref="CreateResult"/>)。任务 10 新增独立的
/// <see cref="ShapeTools"/> 是因为「绘制」是全新能力域;上传不是,故并入本类型而非另起一类。
/// </para>
/// <para>
/// <b>参数与返回一律 snake_case</b>:这是对外契约的一部分,与项目内的 C# 命名习惯无关。
/// 故显式使用 <see cref="JsonPropertyNameAttribute"/> 而非依赖序列化器的命名策略 ——
/// 命名策略是全局开关,任何一处调整都会静默改动全部工具的线上契约。
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ImageTools(ImageBufferStore store)
{
    // —————————————————————— 对外错误码 ——————————————————————

    /// <summary>尺寸非法(小于等于 0、超出单边上限或超出像素总数上限)。</summary>
    private const string CodeInvalidDimension = "invalid_dimension";

    /// <summary>像素格式名非法。</summary>
    private const string CodeInvalidFormat = "invalid_format";

    /// <summary>颜色串非法。</summary>
    private const string CodeInvalidColor = "invalid_color";

    /// <summary>Id 不存在、已被释放或已被回收。</summary>
    private const string CodeImageNotFound = "image_not_found";

    /// <summary>像素坐标越界。</summary>
    private const string CodePixelOutOfRange = "pixel_out_of_range";

    /// <summary>注册表容量超限。</summary>
    private const string CodeCapacityExceeded = "capacity_exceeded";

    /// <summary>上传的输入体积超限(复用既有码:语义与绘制侧完全一致)。</summary>
    private const string CodeLimitExceeded = "limit_exceeded";

    /// <summary>上传的字符串不是合法 base64(含误带 <c>data:</c> URL 前缀的情形)。</summary>
    private const string CodeInvalidBase64 = "invalid_base64";

    /// <summary>上传的不是 PNG,或使用了本服务不支持的 PNG 特性。</summary>
    private const string CodeUnsupportedImage = "unsupported_image";

    /// <summary>上传的是 PNG 但结构不合法(块结构、CRC-32、zlib 流、扫描行数据)。</summary>
    private const string CodeInvalidImage = "invalid_image";

    /// <summary>默认像素格式名。</summary>
    private const string DefaultFormatName = "rgba32";

    /// <summary><c>data:</c> URL 前缀,用于给出「请去掉前缀」这类可执行指引。</summary>
    private const string DataUrlPrefix = "data:";

    // —————————————————————— 工具 1:创建 ——————————————————————

    /// <summary>
    /// 创建一张全 0 的空白内存图像,返回其唯一 Id。
    /// </summary>
    /// <param name="width">图像宽度(像素),须为大于 0 的整数。</param>
    /// <param name="height">图像高度(像素),须为大于 0 的整数。</param>
    /// <param name="format">
    /// 像素格式,取 <c>gray8</c>/<c>rgb24</c>/<c>bgr24</c>/<c>rgba32</c>/<c>bgra32</c>(大小写不敏感),默认 <c>rgba32</c>。
    /// </param>
    [McpServerTool(
        Name = "image_create",
        Title = "创建内存图像",
        Destructive = false,
        OpenWorld = false)]
    [Description("创建一张指定宽高的空白内存图像,返回唯一 id 与图像元信息。返回的 id 需在后续调用中传入,用完请调用 image_release 释放。")]
    public CallToolResult Create(
        [Description("图像宽度(像素),须大于 0 且不超过服务上限")] int width,
        [Description("图像高度(像素),须大于 0 且不超过服务上限")] int height,
        [Description("像素格式,取 gray8 / rgb24 / bgr24 / rgba32 / bgra32(大小写不敏感),默认 rgba32")] string? format = null)
    {
        string formatName = format ?? DefaultFormatName;

        if (!TryParseFormat(formatName, out var pixelFormat))
        {
            return Error(
                CodeInvalidFormat,
                $"不支持的像素格式 \"{formatName}\":期望 gray8 / rgb24 / bgr24 / rgba32 / bgra32"
                + $"(大小写不敏感),实际 \"{formatName}\"。");
        }

        try
        {
            string id = store.Create(width, height, pixelFormat);

            // 元信息一律回读注册表,而非用入参拼装:紧排步长由 ImageBuffer 决定,
            // 在工具层重算一遍等于复制了一份可能失配的规则
            store.TryDescribe(id, out var descriptor);

            return Ok(new CreateResult(
                Ok: true,
                Id: id,
                Width: descriptor.Width,
                Height: descriptor.Height,
                Format: formatName.ToLowerInvariant(),
                Stride: descriptor.Stride,
                ByteLength: descriptor.ByteLength));
        }
        catch (ImageCapacityExceededException ex)
        {
            return Error(CodeCapacityExceeded, ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // 尺寸校验的唯一权威是 ImageBuffer,此处只做翻译。
            // 重复实现一套宽高校验必然会与构造期校验逐渐分叉,而分叉的那一天没有告警
            return Error(CodeInvalidDimension, ex.Message);
        }
    }

    // —————————————————————— 工具 2:上传 ——————————————————————

    /// <summary>
    /// 上传一张 PNG 图像,解码后登记为新的内存图像并返回其唯一 Id。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>校验顺序即防护本身</b>,七道闸门依次为:串长(不解码)→ base64 解码 → 字节长度 →
    /// 文件头解析(零分配)→ 注册表容量预检(零分配)→ <b>此时才分配</b> → 登记。
    /// 把任何两步调换,「先分配再拒绝」就会重新出现。
    /// </para>
    /// <para>
    /// <b>不置 <c>Idempotent</c></b>:每次调用都新建一张图并占用容量,重试会产生多个对象 ——
    /// 与 <see cref="Create"/> 同理,与 <c>image_release</c>(幂等的收尾动作)刻意不同。
    /// </para>
    /// </remarks>
    /// <param name="data">PNG 文件字节的 base64 串,<b>不带</b> <c>data:image/png;base64,</c> 前缀。</param>
    [McpServerTool(
        Name = "image_upload",
        Title = "上传图像",
        Destructive = false,
        OpenWorld = false)]
    [Description("上传一张 PNG 图像(以 base64 传入 PNG 文件字节,不带 data URL 前缀),解码后登记为新的内存图像,返回唯一 id 与图像元信息。返回的 id 与 image_create 同形态,可直接用于 image_draw_* / image_set_pixels / image_download_png;用完请调用 image_release 释放。")]
    public CallToolResult Upload(
        [Description("PNG 文件字节的 base64 字符串(不含 data:image/png;base64, 前缀)")] string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return Error(
                CodeInvalidBase64,
                $"data 不能为空:期望 PNG 文件字节的 base64 字符串,实际 {(data is null ? "null" : "空串")}。");
        }

        // 第一道闸门:只量长度,不解码。先解码再判长度等于闸门形同虚设 ——
        // 超长串的解码本身就会先吃掉一份与输入等大的内存
        if (data.Length > ImageLimits.MaxBase64Length)
        {
            return Error(
                CodeLimitExceeded,
                $"base64 字符串长度 {data.Length} 超过上限 {ImageLimits.MaxBase64Length}"
                + $"(对应原始字节上限 {ImageLimits.MaxInputByteLength} 字节 / {ImageLimits.MaxInputByteLength / (1024 * 1024)} MiB)。");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException ex)
        {
            // 异常不得逃逸:经 SDK 包装后调用方只会拿到一段堆栈文本,而它需要的是「哪里错了、怎么改」
            return Error(CodeInvalidBase64, BuildBase64Message(data, ex));
        }

        // 第二道闸门:解码后的真实字节数。base64 长度校验只能挡住「明显超长」,
        // 带换行/填充的合法串在长度合规的前提下仍可能解出更多字节
        if (bytes.Length > ImageLimits.MaxInputByteLength)
        {
            return Error(
                CodeLimitExceeded,
                $"PNG 原始字节数 {bytes.Length} 超过上限 {ImageLimits.MaxInputByteLength}"
                + $"({ImageLimits.MaxInputByteLength / (1024 * 1024)} MiB)。请先压缩或缩小图像再上传。");
        }

        try
        {
            // 第三道闸门:解析文件头,零像素分配。至此才第一次知道「声明的宽高」
            PngHeader header = PngDecoder.ReadHeader(bytes);

            // 第四道闸门:零分配地撞注册表容量。必须早于 Decode ——
            // 否则一张声明 8192×8192 的图会先吃掉 256 MiB 才被告知「注册表装不下」
            store.EnsureCapacity(header.Width, header.Height, header.Format);

            // 第五道:此时才分配目标缓冲区并逐行填充
            ImageBuffer image = PngDecoder.Decode(bytes, header);

            string id = store.Add(image);

            // 元信息一律回读注册表,而非用 header 拼装 —— 与 image_create 同一条约定
            store.TryDescribe(id, out var descriptor);

            return Ok(new CreateResult(
                Ok: true,
                Id: id,
                Width: descriptor.Width,
                Height: descriptor.Height,
                Format: FormatNameOf(descriptor.Format),
                Stride: descriptor.Stride,
                ByteLength: descriptor.ByteLength));
        }
        catch (UnsupportedImageException ex)
        {
            return Error(CodeUnsupportedImage, ex.Message);
        }
        catch (InvalidImageException ex)
        {
            return Error(CodeInvalidImage, ex.Message);
        }
        catch (ImageCapacityExceededException ex)
        {
            return Error(CodeCapacityExceeded, ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // 尺寸校验的唯一权威是 ImageLimits / ImageBuffer,此处只做翻译(与 image_create 同)
            return Error(CodeInvalidDimension, ex.Message);
        }
    }

    // —————————————————————— 工具 3:释放 ——————————————————————

    /// <summary>
    /// 释放指定的内存图像。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    [McpServerTool(
        Name = "image_release",
        Title = "释放内存图像",
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("释放指定 id 的内存图像。释放后该 id 立即失效,再次访问将返回 image_not_found。对不存在或已释放的 id 调用将返回 ok=false、code=image_not_found。")]
    public CallToolResult Release(
        [Description("image_create 返回的图像 id")] string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return Error(CodeImageNotFound, $"id 不能为空(实际 {(id is null ? "null" : "空串")})。");
        }

        if (!store.TryRelease(id, out _))
        {
            // 释放一个不存在的 Id 报「未找到」,而不是回一个「成功但没释放任何东西」:
            // 后者会让「Id 拼错了」与「确实释放了」在调用方看来完全一样,
            // 而这两者的后续处置截然不同 —— 前者需要回头修正 Id,后者可以直接继续
            return Error(
                CodeImageNotFound,
                $"未找到内存图像对象(Id: {id})。该对象可能从未创建、已被释放,或已因空闲超时被回收。");
        }

        return Ok(new ReleaseResult(Ok: true, Id: id, Released: true));
    }

    // —————————————————————— 工具 4:写入像素 ——————————————————————

    /// <summary>
    /// 批量写入像素颜色。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    /// <param name="points">待写入的点集合,元素形如 <c>{ "x": 1, "y": 2, "color": "#FF0000" }</c>。</param>
    [McpServerTool(
        Name = "image_set_pixels",
        Title = "设置像素颜色",
        Destructive = true,
        OpenWorld = false)]
    [Description("按 id 向内存图像批量写入像素颜色。points 为 {x, y, color} 数组,color 取 #RRGGBB 或 #RRGGBBAA。任一坐标越界或颜色非法时整批拒绝、不写入任何像素。")]
    public CallToolResult SetPixels(
        [Description("image_create 返回的图像 id")] string? id,
        [Description("待写入的点数组,元素形如 {\"x\":1,\"y\":2,\"color\":\"#FF0000\"}")] IReadOnlyList<PixelPoint>? points)
    {
        if (string.IsNullOrEmpty(id))
        {
            return Error(CodeImageNotFound, $"id 不能为空(实际 {(id is null ? "null" : "空串")})。");
        }

        if (points is null || points.Count == 0)
        {
            return Error(
                CodePixelOutOfRange,
                $"points 不能为空:期望至少 1 个 {{\"x\",\"y\",\"color\"}} 元素,实际 {(points is null ? "null" : "空数组")}。");
        }

        // 先把全部颜色解析完再落库。颜色解析若放到写入循环里,第 N 个点颜色非法时
        // 前 N-1 个点已经写进去了 —— 正是本工具明令禁止的半成功形态
        var assignments = new PixelAssignment[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            PixelPoint point = points[i];

            if (!ColorText.TryParseColor(point.Color, out var color))
            {
                return Error(
                    CodeInvalidColor,
                    $"第 {i} 个点的 color 非法:期望 #RRGGBB 或 #RRGGBBAA(大小写不敏感、必须带 #),"
                    + $"实际 {Describe(point.Color)}。");
            }

            assignments[i] = new PixelAssignment(point.X, point.Y, color);
        }

        try
        {
            int updated = store.SetPixels(id, assignments);
            return Ok(new SetPixelsResult(Ok: true, Id: id, Updated: updated));
        }
        catch (ImageNotFoundException ex)
        {
            return Error(CodeImageNotFound, ex.Message);
        }
        catch (PixelOutOfRangeException ex)
        {
            return Error(CodePixelOutOfRange, ex.Message);
        }
    }

    // —————————————————————— 工具 5:导出 PNG ——————————————————————

    /// <summary>
    /// 把指定图像编码为 PNG,并以图像内容块返回。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    [McpServerTool(
        Name = "image_download_png",
        Title = "下载 PNG 图片",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("把指定 id 的内存图像编码为 PNG,直接以图像内容返回(不提供下载链接)。")]
    public CallToolResult DownloadPng(
        [Description("image_create 返回的图像 id")] string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return Error(CodeImageNotFound, $"id 不能为空(实际 {(id is null ? "null" : "空串")})。");
        }

        try
        {
            byte[] png = store.EncodePng(id);

            // FromBytes 负责 base64 编码与 MIME 标注,与手写 Convert.ToBase64String 等价;
            // 用 SDK 的工厂是为了让「Data 存 base64、DecodedData 存原始字节」这一约定只有一处实现
            return new CallToolResult
            {
                Content = [ImageContentBlock.FromBytes(png, "image/png")],
                IsError = false
            };
        }
        catch (ImageNotFoundException ex)
        {
            return Error(CodeImageNotFound, ex.Message);
        }
    }

    // —————————————————————— 结果构造 ——————————————————————

    /// <summary>构造成功结果:内容为一段 snake_case JSON 文本。</summary>
    private static CallToolResult Ok<T>(T payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
        IsError = false
    };

    /// <summary>构造失败结果。<see cref="CallToolResult.IsError"/> 置位,使调用方能识别为失败。</summary>
    private static CallToolResult Error(string code, string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new ErrorResult(false, code, message)) }],
        IsError = true
    };

    /// <summary>把可空串渲染成便于定位的形态,避免错误消息里出现「实际:」后一片空白。</summary>
    private static string Describe(string? value) => value is null ? "null" : $"\"{value}\"";

    /// <summary>
    /// 为 base64 解析失败构造可执行的消息。
    /// </summary>
    /// <remarks>
    /// <b>为何不静默剥离 <c>data:</c> 前缀</b>:静默剥离会让「输入到底是什么」这一契约变成两可 ——
    /// 调用方永远学不会正确的传法,而服务端则要多维护一条没有文档的宽容路径。
    /// 明确拒绝并给出可照抄的指引,是这里唯一能让契约保持单一形态的做法。
    /// 截断回显串是为了避免把一段几 MB 的 base64 原样塞回错误消息。
    /// </remarks>
    private static string BuildBase64Message(string data, FormatException ex)
    {
        string head = data.Length <= 32 ? data : data[..32] + "…";

        if (data.TrimStart().StartsWith(DataUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "data 不是合法的 base64:看起来带上了 data URL 前缀。"
                + "请去掉 \"data:image/png;base64,\" 这一段,只传 PNG 原始字节的 base64。"
                + $"实际以 {Describe(head)} 开头。";
        }

        return "data 不是合法的 base64:期望 PNG 文件字节的 base64 字符串"
            + $"(仅含 A-Z a-z 0-9 + / 与末尾的 = 填充,不含 data URL 前缀、不含换行以外的其它字符)。"
            + $"实际以 {Describe(head)} 开头,解析失败原因:{ex.Message}";
    }

    /// <summary>求像素格式的规范化对外名称(小写),与 <see cref="TryParseFormat"/> 互为逆运算。</summary>
    /// <exception cref="ArgumentOutOfRangeException">格式不是已定义的像素格式。</exception>
    private static string FormatNameOf(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => "gray8",
        PixelFormat.Rgb24 => "rgb24",
        PixelFormat.Bgr24 => "bgr24",
        PixelFormat.Rgba32 => "rgba32",
        PixelFormat.Bgra32 => "bgra32",
        // 未定义格式必须立即失败:静默兜底会让调用方拿到一个它无法在后续调用中使用的格式名
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"未知的像素格式({(int)format}),无法确定对外名称。")
    };

    // —————————————————————— 输入解析 ——————————————————————

    /// <summary>
    /// 解析像素格式名(大小写不敏感)。
    /// </summary>
    /// <remarks>
    /// <b>刻意不用 <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/></b>:它对数字串同样返回成功,
    /// <c>"3"</c> 会被解析成 <c>Rgba32</c>,<c>"99"</c> 会解析出一个根本不存在的枚举值。
    /// 前者让「格式名」这一契约悄悄容纳了「格式序号」,后者则会让非法值一路流到像素寻址处。
    /// 白名单式匹配是唯一能把这两条都堵死的形式。
    /// </remarks>
    private static bool TryParseFormat(string name, out PixelFormat format)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "gray8":
                format = PixelFormat.Gray8;
                return true;
            case "rgb24":
                format = PixelFormat.Rgb24;
                return true;
            case "bgr24":
                format = PixelFormat.Bgr24;
                return true;
            case "rgba32":
                format = PixelFormat.Rgba32;
                return true;
            case "bgra32":
                format = PixelFormat.Bgra32;
                return true;
            default:
                format = default;
                return false;
        }
    }

    // 颜色解析(TryParseColor / IsHexDigit / ParseHexByte)已抽到 ColorText:
    // 绘制工具需要同一套解析,留在本类型里只会让两份实现逐渐分叉。
    // 该抽取是纯搬移,本类型的行为、错误文案与线上契约逐字未变。
}

// —————————————————————— 对外 JSON 契约 ——————————————————————

/// <summary>
/// <c>image_set_pixels</c> 的单个点参数:<c>{ "x", "y", "color" }</c>。
/// </summary>
/// <param name="X">列号。</param>
/// <param name="Y">行号。</param>
/// <param name="Color"><c>#RRGGBB</c> 或 <c>#RRGGBBAA</c> 形式的颜色串。</param>
public sealed record PixelPoint(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("color")] string? Color);

/// <summary>失败结果:<c>{ ok, code, message }</c>。</summary>
/// <param name="Ok">恒为 <c>false</c>。</param>
/// <param name="Code">机器可判别的错误码。</param>
/// <param name="Message">面向调用方的说明,含期望值与实际值。</param>
public sealed record ErrorResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary><c>image_create</c> 的成功结果。</summary>
/// <param name="Ok">恒为 <c>true</c>。</param>
/// <param name="Id">唯一标识。</param>
/// <param name="Width">宽度(像素)。</param>
/// <param name="Height">高度(像素)。</param>
/// <param name="Format">规范化后的格式名(小写)。</param>
/// <param name="Stride">行步长(字节)。</param>
/// <param name="ByteLength">有效像素字节数。</param>
public sealed record CreateResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("stride")] int Stride,
    [property: JsonPropertyName("byteLength")] long ByteLength);

/// <summary><c>image_release</c> 的成功结果。</summary>
/// <param name="Ok">恒为 <c>true</c>。</param>
/// <param name="Id">目标标识。</param>
/// <param name="Released">
/// 恒为 <c>true</c> —— 释放不存在的 Id 走的是失败分支(<c>ok:false</c>),不会产出本结构。
/// 保留该字段是为了让成功响应的形状在不同工具间保持一致,便于调用方统一解析。
/// </param>
public sealed record ReleaseResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("released")] bool Released);

/// <summary><c>image_set_pixels</c> 的成功结果。</summary>
/// <param name="Ok">恒为 <c>true</c>。</param>
/// <param name="Id">目标标识。</param>
/// <param name="Updated">实际写入的像素数。</param>
public sealed record SetPixelsResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("updated")] int Updated);

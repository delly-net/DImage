using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DImage.Api.Imaging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DImage.Api.Mcp;

/// <summary>
/// 绘制与合成能力的七项 MCP 工具:直线/折线、矩形、椭圆/扇形、多边形、SVG 路径、文字、图像合成。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型与 <see cref="ImageTools"/> 同属工具层,且与其一样不直接触碰 <see cref="ImageBuffer"/></b>:
/// 绘制一律经 <see cref="ImageBufferStore.Draw"/> 在条目锁内完成。工具层若自行 <c>TryGet</c>
/// 出缓冲区再操作,等于把并发写入的竞态从注册表里放了出来。
/// </para>
/// <para>
/// <b>两种「越界」语义必须分清</b> —— 这是本类型最容易被误按直觉改动的地方:
/// <list type="bullet">
///   <item><description><b>越界裁剪</b>:坐标是合法有限值但落在画布外(含部分在外)时<b>正常绘制</b>,
///   画布内部分落像素、画布外部分丢弃,返回 <c>ok:true</c>;</description></item>
///   <item><description><b>报错</b>:颜色串非法、线宽非法、半径或宽高非正、顶点数不足、
///   <c>fill_rule</c> 非法、<c>d</c> 语法错误、超上限、坐标非有限值或超量级、Id 不存在。</description></item>
/// </list>
/// 这与 <see cref="ImageTools"/> 的 <c>image_set_pixels</c>(任一点越界即整批拒绝)<b>语义不同</b>。
/// 该差异是刻意选择的:几何图形天然会部分越出画布(画一条横穿画布的线),拒绝它没有意义;
/// 而逐像素写入的越界则几乎总是调用方算错了坐标。<b>请勿按字面把两者统一</b>。
/// </para>
/// <para>
/// <b>绘制工具是非幂等的</b>:抗锯齿下「覆盖一半」的像素重复绘制会逐步逼近前景色,
/// 故同一形状画两次的图像与画一次<b>不逐字节相同</b>。MCP 客户端常默认工具可安全重试,
/// 这里的 <c>Idempotent</c> 因此<b>刻意不置位</b>;调用方需要「重来一次」的语义时,
/// 应 <c>image_release</c> 后重建,或改用 <c>image_download_png</c> 校验结果。
/// </para>
/// <para>
/// <b><c>image_composite</c> 是第七项工具,也是本类型里唯一「一次动两张图」的工具</b>:
/// 它按 <c>target_id</c> 与 <c>source_id</c> 取两张图像,把源图按落点、缩放与不透明度贴到目标图上。
/// 以下三点与其余六项不同,改动前务必先读懂:
/// <list type="bullet">
///   <item><description><b>两个 Id 可以相同</b>(自合并),此时注册表先对源取快照,结果等同于用合成前的自身作源,
///   与一般合成<b>一样是非幂等的</b> —— 落点非原点时,自合并是把图像与其<b>位移后的副本</b>按覆盖率混合,
///   再执行一次会读到上一轮的结果并不会回到原样(验收 A10 用「逐轮各自 Clone」的对照组钉死这一点)。
///   只有落点为原点、缩放 1、不透明度 1 时,自合并才恰好是恒等映射;</description></item>
///   <item><description><b>缩放是最近邻</b>,不做双线性加权:放大后的边缘会有锯齿,但结果可被逐像素复现。
///   这是刻意取舍 —— 本能力域其余部分(颜色、覆盖率、格式折叠)全部是确定性的,
///   引入一个只会产生「看起来差不多」的中间色的环节,等于让验收失去判据;</description></item>
///   <item><description><b>不透明度走的是覆盖率插值,不是标准 <c>source-over</c></b>,
///   与六项绘制工具共用同一份混合实现(Alpha 通道一并插值)。详见 <c>ImageBlend</c> 的类型注释 ——
///   它是本能力域最容易被「顺手修正」而实际造成前后不一致的一处。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>参数与返回一律 snake_case</b>:这是对外契约的一部分,与项目内的 C# 命名习惯无关。
/// 但两条路径不同,原因必须写清楚,否则后来者会把它们「统一」成同一种写法:
/// <list type="bullet">
///   <item><description><b>参数</b>直接以 snake_case 作为 C# 标识符(<c>stroke_width</c>、<c>fill_rule</c>、
///   <c>start_angle</c>、<c>end_angle</c>)。这不是风格选择,而是<b>唯一可行</b>的选择:
///   <see cref="JsonPropertyNameAttribute"/> 的 <c>AttributeUsage</c> 只含 <c>Property</c> 与 <c>Field</c>,
///   标在参数上是编译错误 CS0592。而工具 <c>inputSchema</c> 的属性名取自参数名本身,
///   故要让调用方看到 <c>stroke_width</c>,参数就只能叫这个名字。</description></item>
///   <item><description><b>返回记录</b>用常规 C# 命名 + <c>[property: JsonPropertyName]</c>:
///   它们可以标注,且不该为了序列化而放弃常规命名。</description></item>
/// </list>
/// 两条路径共同拒绝的是同一样东西:依赖序列化器的全局命名策略 ——
/// 那等于把线上契约交给一个全局开关,任何一处调整都会静默改动全部工具。
/// </para>
/// <para>
/// <b>失败一律以结构化结果返回,不靠抛异常</b>:异常经 SDK 包装后调用方拿到的是一段难以结构化
/// 处理的堆栈文本,而 MCP 工具的调用方是语言模型,它需要的是「哪个参数错了、期望什么、实际什么」。
/// </para>
/// <para>
/// <b>三个私有辅助方法(<c>Ok</c> / <c>Error</c> / <c>Describe</c>)与 <see cref="ImageTools"/> 重复</b>,
/// 这是<b>刻意的</b>:把它们提到某个公共基类或静态类,就要动 <see cref="ImageTools"/> 的类形态,
/// 而本次重构被严格限定为「只抽颜色解析」这一处,好让既有 4 项工具的线上契约有据可查地逐字未变。
/// 三行字符串处理的分叉成本,远低于动一份已被验收过的契约代码的风险。
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ShapeTools(ImageBufferStore store)
{
    // —————————————————————— 对外错误码 ——————————————————————
    //
    // 全部为本次新增,与既有的 invalid_dimension / invalid_format / invalid_color /
    // image_not_found / pixel_out_of_range / capacity_exceeded 六个码不冲突
    // (invalid_color 与 image_not_found 为复用,语义完全一致)。
    // 末两个(invalid_scale / invalid_opacity)随 image_composite 新增,同样不复用绘制侧的码。

    /// <summary>颜色串非法。</summary>
    private const string CodeInvalidColor = "invalid_color";

    /// <summary>几何参数非法:顶点数不足、半径或宽高非正、坐标非有限值或超量级、起止角只给其一。</summary>
    private const string CodeInvalidGeometry = "invalid_geometry";

    /// <summary>线宽非法:非有限值、小于等于 0,或超上限。</summary>
    private const string CodeInvalidStrokeWidth = "invalid_stroke_width";

    /// <summary>填充规则名非法。</summary>
    private const string CodeInvalidFillRule = "invalid_fill_rule";

    /// <summary><c>d</c> 字符串语法错误。</summary>
    private const string CodeInvalidPath = "invalid_path";

    /// <summary>超出绘制上限(<c>d</c> 长度、命令数、扁平化段数)。</summary>
    private const string CodeLimitExceeded = "limit_exceeded";

    /// <summary>Id 不存在、已被释放或已被回收。</summary>
    private const string CodeImageNotFound = "image_not_found";

    /// <summary>缩放倍数非法:非有限值,或小于等于 0。</summary>
    private const string CodeInvalidScale = "invalid_scale";

    /// <summary>不透明度非法:非有限值,或落在 <c>[0, 1]</c> 之外。</summary>
    private const string CodeInvalidOpacity = "invalid_opacity";

    /// <summary>默认填充规则名。</summary>
    private const string DefaultFillRuleName = "nonzero";

    /// <summary>默认文本对齐方式名。</summary>
    private const string DefaultAnchorName = "left";

    /// <summary>仿射矩阵的分量个数。</summary>
    private const int TransformComponentCount = 6;

    // 五条工具描述里反复出现的两句说明,抽成常量并在 [Description] 中拼接:
    // 二者是「与 image_set_pixels 语义不同」的全部要点,也是验收 A34 的断言对象。
    // 写成常量后措辞只有一处,改动不会漏掉某条工具;且漏拼会在描述里立刻显形
    private const string ClipNote =
        "越界裁剪:形状超出画布的部分被丢弃并返回成功,不报错(与 image_set_pixels 的越界即拒绝相反)。";

    private const string NonIdempotentNote =
        "非幂等:抗锯齿下重复绘制同一形状会加深半覆盖像素,重试前请先 image_release 重建。";

    // image_composite 的四句说明。它们各自只出现在一条工具描述里,不像上面两句那样跨工具复用,
    // 但仍写成常量:[Description] 要把四句拼成一行,内联字面量会让那行长达数百字符且无从校对,
    // 而描述字符串是对外契约的一部分(与 A34 同类断言的对象),必须能一眼读完、逐句核对。
    // 刻意不扩充既有的 ClipNote / NonIdempotentNote:它们写的是「形状」,照搬到图像合成上
    // 会让描述与行为对不上;而改写它们则会静默改动既有六项工具的线上描述。

    /// <summary>合成工具的越界说明。<b>与 <see cref="ClipNote"/> 分开</b>,因为两者措辞不同。</summary>
    private const string CompositeClipNote =
        "越界裁剪:源图超出目标画布的部分被丢弃并返回成功,不报错(与 image_set_pixels 的越界即拒绝相反)。";

    /// <summary>合成工具的不透明度说明。</summary>
    private const string CompositeOpacityNote =
        "不透明度按覆盖率逐通道插值(Alpha 通道一并插值),不是标准 source-over;"
        + "opacity 为 0 或源像素全透明时该像素不被触碰,亦不计入 covered。";

    /// <summary>合成工具的同 Id 说明。</summary>
    private const string CompositeSameIdNote =
        "target_id 与 source_id 可以相同:此时先对源取快照再合成,结果等同于用合成前的自身作源。";

    /// <summary>合成工具的幂等性说明。</summary>
    /// <remarks>
    /// <b>不写「自合并除外」</b>:同 Id 自合并同样是非幂等的(落点非原点时是与自己的位移副本混合),
    /// 只有「落点原点 + 缩放 1 + 不透明度 1」才是恒等映射。把自合并说成幂等会让调用方放心重试,
    /// 而那正是本句要阻止的事。
    /// </remarks>
    private const string CompositeRetryNote =
        "非幂等:半透明叠加重复执行会逐次逼近源图颜色(同 Id 自合并亦然),重试前请先 image_release 重建。";

    // —————————————————————— 工具 1:直线 / 折线 ——————————————————————

    /// <summary>
    /// 绘制直线或折线(只描边,不填充)。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    /// <param name="points">顶点序列,至少 2 个;2 点即直线,n 点即折线(不闭合)。</param>
    /// <param name="color"><c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。</param>
    /// <param name="stroke_width">线宽(像素)。</param>
    /// <param name="antialias">是否抗锯齿。</param>
    [McpServerTool(
        Name = "image_draw_line",
        Title = "绘制直线/折线",
        Destructive = true,
        OpenWorld = false)]
    [Description("按 id 在内存图像上绘制直线或折线:points 为顶点序列(≥2 个,2 点即直线、n 点即折线,不闭合),只描边不填充。坐标允许亚像素。" + ClipNote + NonIdempotentNote + "成功返回 {ok,id,covered}。")]
    public CallToolResult DrawLine(
        [Description("image_create 返回的图像 id")] string? id,
        [Description("顶点序列,至少 2 个,元素形如 {\"x\":0,\"y\":0},坐标为 double")] IReadOnlyList<ShapePoint>? points,
        [Description("颜色,#RRGGBB 或 #RRGGBBAA")] string? color,
        [Description("线宽(像素),须大于 0 且不超过 4096,默认 1")] double stroke_width = 1,
        [Description("是否抗锯齿,默认 true;关闭后像素颜色非前景即背景")] bool antialias = true)
    {
        if (points is null)
        {
            return Error(
                CodeInvalidGeometry,
                "points 不能为空:期望至少 2 个 {\"x\",\"y\"} 顶点(2 点即直线、n 点即折线),实际 null。");
        }

        if (BuildStyle(color, stroke_width, stroke: true, fill: false, FillRule.NonZero, antialias, out var style) is { } error)
        {
            return error;
        }

        return Execute(id, new LineShape(ToPoints(points)), style);
    }

    // —————————————————————— 工具 2:矩形 ——————————————————————

    /// <summary>
    /// 绘制轴对齐矩形。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    /// <param name="x">左上角横坐标。</param>
    /// <param name="y">左上角纵坐标。</param>
    /// <param name="width">宽度,须大于 0。</param>
    /// <param name="height">高度,须大于 0。</param>
    /// <param name="color"><c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。</param>
    /// <param name="stroke_width">线宽(像素)。</param>
    /// <param name="fill">是否填充内部。</param>
    /// <param name="antialias">是否抗锯齿。</param>
    [McpServerTool(
        Name = "image_draw_rect",
        Title = "绘制矩形",
        Destructive = true,
        OpenWorld = false)]
    [Description("按 id 在内存图像上绘制轴对齐矩形(左上角 x/y 加宽高,不支持旋转,任意四边形请用 image_draw_polygon)。" + ClipNote + NonIdempotentNote + "成功返回 {ok,id,covered}。")]
    public CallToolResult DrawRect(
        [Description("image_create 返回的图像 id")] string? id,
        [Description("左上角横坐标(允许亚像素)")] double x,
        [Description("左上角纵坐标(允许亚像素)")] double y,
        [Description("宽度(像素),须大于 0")] double width,
        [Description("高度(像素),须大于 0")] double height,
        [Description("颜色,#RRGGBB 或 #RRGGBBAA")] string? color,
        [Description("线宽(像素),须大于 0 且不超过 4096,默认 1")] double stroke_width = 1,
        [Description("是否填充内部,默认 false(仅描边)")] bool fill = false,
        [Description("是否抗锯齿,默认 true;关闭后像素颜色非前景即背景")] bool antialias = true)
    {
        if (BuildStyle(color, stroke_width, stroke: true, fill, FillRule.NonZero, antialias, out var style) is { } error)
        {
            return error;
        }

        return Execute(id, new RectShape(x, y, width, height), style);
    }

    // —————————————————————— 工具 3:椭圆 / 扇形 ——————————————————————

    /// <summary>
    /// 绘制椭圆、正圆或扇形。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    /// <param name="cx">中心横坐标。</param>
    /// <param name="cy">中心纵坐标。</param>
    /// <param name="rx">横半轴,须大于 0。</param>
    /// <param name="ry">纵半轴,须大于 0。</param>
    /// <param name="color"><c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。</param>
    /// <param name="start_angle">起始角(度),与 <paramref name="end_angle"/> 同时给出时为扇形。</param>
    /// <param name="end_angle">结束角(度)。</param>
    /// <param name="stroke_width">线宽(像素)。</param>
    /// <param name="fill">是否填充内部。</param>
    /// <param name="fill_rule">填充规则名。</param>
    /// <param name="antialias">是否抗锯齿。</param>
    [McpServerTool(
        Name = "image_draw_ellipse",
        Title = "绘制椭圆/扇形",
        Destructive = true,
        OpenWorld = false)]
    [Description("按 id 在内存图像上绘制椭圆:rx 等于 ry 即正圆;同时给出 start_angle 与 end_angle 时绘制扇形(闭合到圆心)。角度单位为度,0° 指向 +x 轴、角度增大方向为顺时针(与屏幕坐标系一致,与数学课本的逆时针相反)。" + ClipNote + NonIdempotentNote + "成功返回 {ok,id,covered}。")]
    public CallToolResult DrawEllipse(
        [Description("image_create 返回的图像 id")] string? id,
        [Description("中心横坐标(允许亚像素)")] double cx,
        [Description("中心纵坐标(允许亚像素)")] double cy,
        [Description("横半轴(像素),须大于 0;与 ry 相等即正圆")] double rx,
        [Description("纵半轴(像素),须大于 0")] double ry,
        [Description("颜色,#RRGGBB 或 #RRGGBBAA")] string? color,
        [Description("起始角(度,顺时针为正,0° 指向 +x 轴);与 end_angle 同时给出即为扇形,同时省略即整椭圆")] double? start_angle = null,
        [Description("结束角(度,顺时针为正)")] double? end_angle = null,
        [Description("线宽(像素),须大于 0 且不超过 4096,默认 1")] double stroke_width = 1,
        [Description("是否填充内部,默认 false(仅描边)")] bool fill = false,
        [Description("填充规则,取 nonzero(默认)或 evenodd,大小写不敏感")] string? fill_rule = DefaultFillRuleName,
        [Description("是否抗锯齿,默认 true;关闭后像素颜色非前景即背景")] bool antialias = true)
    {
        if (!FillRuleExtensions.TryParseFillRule(fill_rule, out var parsedFillRule))
        {
            return InvalidFillRule(fill_rule);
        }

        if (BuildStyle(color, stroke_width, stroke: true, fill, parsedFillRule, antialias, out var style) is { } error)
        {
            return error;
        }

        return Execute(id, new EllipseShape(cx, cy, rx, ry, start_angle, end_angle), style);
    }

    // —————————————————————— 工具 4:多边形 ——————————————————————

    /// <summary>
    /// 绘制多边形(自动闭合)。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    /// <param name="points">顶点序列,至少 3 个;末顶点自动回到首顶点。</param>
    /// <param name="color"><c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。</param>
    /// <param name="stroke_width">线宽(像素)。</param>
    /// <param name="fill">是否填充内部。</param>
    /// <param name="fill_rule">填充规则名。</param>
    /// <param name="antialias">是否抗锯齿。</param>
    [McpServerTool(
        Name = "image_draw_polygon",
        Title = "绘制多边形",
        Destructive = true,
        OpenWorld = false)]
    [Description("按 id 在内存图像上绘制任意顶点多边形:points 为顶点序列(≥3 个),末顶点自动回到首顶点闭合。fill_rule 决定自相交区域的内部判定(nonzero 全部填充、evenodd 重叠处成洞)。" + ClipNote + NonIdempotentNote + "成功返回 {ok,id,covered}。")]
    public CallToolResult DrawPolygon(
        [Description("image_create 返回的图像 id")] string? id,
        [Description("顶点序列,至少 3 个,元素形如 {\"x\":0,\"y\":0},坐标为 double;自动闭合")] IReadOnlyList<ShapePoint>? points,
        [Description("颜色,#RRGGBB 或 #RRGGBBAA")] string? color,
        [Description("线宽(像素),须大于 0 且不超过 4096,默认 1")] double stroke_width = 1,
        [Description("是否填充内部,默认 false(仅描边)")] bool fill = false,
        [Description("填充规则,取 nonzero(默认)或 evenodd,大小写不敏感")] string? fill_rule = DefaultFillRuleName,
        [Description("是否抗锯齿,默认 true;关闭后像素颜色非前景即背景")] bool antialias = true)
    {
        if (points is null)
        {
            return Error(
                CodeInvalidGeometry,
                "points 不能为空:期望至少 3 个 {\"x\",\"y\"} 顶点(多边形自动闭合),实际 null。");
        }

        if (!FillRuleExtensions.TryParseFillRule(fill_rule, out var parsedFillRule))
        {
            return InvalidFillRule(fill_rule);
        }

        if (BuildStyle(color, stroke_width, stroke: true, fill, parsedFillRule, antialias, out var style) is { } error)
        {
            return error;
        }

        return Execute(id, new PolygonShape(ToPoints(points)), style);
    }

    // —————————————————————— 工具 5:SVG 路径 ——————————————————————

    /// <summary>
    /// 绘制 SVG path。
    /// </summary>
    /// <param name="id">目标图像 Id。</param>
    /// <param name="d">SVG path 的 <c>d</c> 字符串。</param>
    /// <param name="color"><c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。</param>
    /// <param name="stroke_width">线宽(像素)。</param>
    /// <param name="fill">是否填充内部。</param>
    /// <param name="fill_rule">填充规则名。</param>
    /// <param name="antialias">是否抗锯齿。</param>
    [McpServerTool(
        Name = "image_draw_path",
        Title = "绘制 SVG 路径",
        Destructive = true,
        OpenWorld = false)]
    [Description("按 id 在内存图像上绘制 SVG path:d 取完整命令集 M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z,支持隐式重复命令、相对坐标与多重子路径。d 语法错误返回 invalid_path(消息含出错偏移量),超出长度或命令数上限返回 limit_exceeded。" + ClipNote + NonIdempotentNote + "成功返回 {ok,id,covered}。")]
    public CallToolResult DrawPath(
        [Description("image_create 返回的图像 id")] string? id,
        [Description("SVG path 的 d 字符串,如 \"M0 0 L10 0 L10 10 Z\";最长 65536 字符、最多 16384 条命令")] string? d,
        [Description("颜色,#RRGGBB 或 #RRGGBBAA")] string? color,
        [Description("线宽(像素),须大于 0 且不超过 4096,默认 1")] double stroke_width = 1,
        [Description("是否填充内部,默认 false(仅描边)")] bool fill = false,
        [Description("填充规则,取 nonzero(默认)或 evenodd,大小写不敏感")] string? fill_rule = DefaultFillRuleName,
        [Description("是否抗锯齿,默认 true;关闭后像素颜色非前景即背景")] bool antialias = true)
    {
        // d 为 null 交给 PathShape/解析器报 invalid_path,而不是在此另设一个错误码:
        // 「空路径」与「语法错误」对调用方是同一类处置(改 d),分开只会多一个码要记
        if (d is null)
        {
            return Error(
                CodeInvalidPath,
                "d 不能为空:期望 SVG path 字符串(如 \"M0 0 L10 0 L10 10 Z\"),实际 null。");
        }

        if (!FillRuleExtensions.TryParseFillRule(fill_rule, out var parsedFillRule))
        {
            return InvalidFillRule(fill_rule);
        }

        if (BuildStyle(color, stroke_width, stroke: true, fill, parsedFillRule, antialias, out var style) is { } error)
        {
            return error;
        }

        return Execute(id, new PathShape(d), style);
    }

    // —————————————————————— 工具 6:文字 ——————————————————————

    /// <summary>
    /// 绘制文字(只描边,不填充)。
    /// </summary>
    /// <remarks>
    /// <b>整段文本是一次绘制</b>:它虽然有上千条折线子路径,但只调用一次 <see cref="ImageBufferStore.Draw"/>。
    /// 若改成逐字绘制,笔画交叉处的像素会被混合两次 —— 汉字撇捺交叉点出现暗斑,
    /// 而返回的 <c>covered</c> 也会随之偏大。故这里传的是<b>一个</b><see cref="TextShape"/>。
    /// </remarks>
    /// <param name="id">目标图像 Id。</param>
    /// <param name="text">待绘制文本。</param>
    /// <param name="x">文本原点横坐标。</param>
    /// <param name="y">文本原点纵坐标,即首行基线。</param>
    /// <param name="color"><c>#RRGGBB</c> 或 <c>#RRGGBBAA</c>。</param>
    /// <param name="size">字号,即 1 em 的像素高度。</param>
    /// <param name="stroke_width">线宽(像素)。</param>
    /// <param name="letter_spacing">字距(像素)。</param>
    /// <param name="line_spacing">行距(像素);省略取 <see cref="TextLimits.DefaultLineHeightRatio"/> × 字号。</param>
    /// <param name="anchor">水平对齐方式名。</param>
    /// <param name="transform">仿射矩阵六分量。</param>
    /// <param name="antialias">是否抗锯齿。</param>
    [McpServerTool(
        Name = "image_draw_text",
        Title = "绘制文字",
        Destructive = true,
        OpenWorld = false)]
    [Description("按 id 在内存图像上绘制文字:每个字符按内置单线笔画字库展开为笔画后描边(仅描边,永不填充)。size 是 1 em 的像素高度(拉丁按大写字母高度归一化、汉字按设计框);x/y 是首行基线起点,多行时每行各自按 anchor 对齐(left 行首 / center 行中 / right 行尾落在 x,y 逐行下移 line_spacing);文本中 \\n 换行、\\r\\n 与 \\r 等价于 \\n、\\t 展开为 4 个空格;transform 取 6 个分量 [a,b,c,d,e,f],语义同 SVG matrix(a,b,c,d,e,f)(x'=a·x+c·y+e、y'=b·x+d·y+f),作用在已按 size 缩放的像素坐标上、绕文本原点进行。字库未收录的字符(中文标点、emoji 等)以豆腐块方框占位而不报错,该方框同样占用 1 em 宽度。整段文本按一次绘制完成,故笔画交叉处不会因多次混合而加深。" + ClipNote + NonIdempotentNote + "成功返回 {ok,id,covered}。")]
    public CallToolResult DrawText(
        [Description("image_create 返回的图像 id")] string? id,
        [Description("要绘制的文本,最长 4096 字符;\\n 换行、\\t 展开为 4 个空格")] string? text,
        [Description("文本原点横坐标(允许亚像素);锚在行的哪一侧由 anchor 决定")] double x,
        [Description("文本原点纵坐标,即首行基线的位置;后续行按 line_spacing 向下排布")] double y,
        [Description("颜色,#RRGGBB 或 #RRGGBBAA")] string? color,
        [Description("字号(像素),即 1 em 的高度;须大于 0 且不超过 4096,默认 16")] double size = 16,
        [Description("线宽(像素),须大于 0 且不超过 4096,默认 1")] double stroke_width = 1,
        [Description("字距(像素),加在每个字形之后(含末字形),负值收紧字距,默认 0")] double letter_spacing = 0,
        [Description("行距(像素),须大于 0;省略则取字号的 1.2 倍")] double? line_spacing = null,
        [Description("水平对齐方式,取 left(默认)、center 或 right,大小写不敏感;逐行生效,故 center 下各短行左端不齐平")] string? anchor = DefaultAnchorName,
        [Description("仿射变换矩阵,须为 6 个 double:[a,b,c,d,e,f],语义同 SVG matrix(a,b,c,d,e,f);省略即不变换")] IReadOnlyList<double>? transform = null,
        [Description("是否抗锯齿,默认 true;关闭后像素颜色非前景即背景")] bool antialias = true)
    {
        // text 为 null 归到 invalid_geometry:与 image_draw_path 把 null 的 d 归到 invalid_path 同理,
        // 「没有文本」与「文本参数写错」对调用方是同一类处置(改 text),分开只会多一个码要记
        if (text is null)
        {
            return Error(
                CodeInvalidGeometry,
                "text 不能为空:期望要绘制的文本(如 \"Hello 世界\"),实际 null。");
        }

        if (!TextAnchorExtensions.TryParseAnchor(anchor, out var parsedAnchor))
        {
            // anchor 沿用 invalid_geometry 而非新立一个码:它是几何定位属性,
            // 且新码会让调用方多记一个与 invalid_fill_rule 高度相似的失败形态
            return Error(
                CodeInvalidGeometry,
                $"anchor 非法:期望 left、center 或 right(大小写不敏感),实际 {Describe(anchor)}。");
        }

        if (BuildTransform(transform, out var parsedTransform) is { } transformError)
        {
            return transformError;
        }

        // 文字只有描边这一种形态:单线笔画字库没有可填充的内部,
        // 故 fill 恒为 false 且不开放 fill_rule —— 开放一个永远无效的参数只会误导调用方
        if (BuildStyle(color, stroke_width, stroke: true, fill: false, FillRule.NonZero, antialias, out var style) is { } error)
        {
            return error;
        }

        return Execute(
            id,
            new TextShape(text, x, y, size, letter_spacing, line_spacing, parsedAnchor, parsedTransform),
            style);
    }

    // —————————————————————— 工具 7:图像合成 ——————————————————————

    /// <summary>
    /// 把一个 Id 的图像贴到另一个 Id 的图像的指定位置上,支持缩放与半透明叠加。
    /// </summary>
    /// <param name="target_id">目标图像 Id,会被就地修改。</param>
    /// <param name="source_id">源图像 Id,可与 <paramref name="target_id"/> 相同。</param>
    /// <param name="x">源图左上角在目标图中的横坐标。</param>
    /// <param name="y">源图左上角在目标图中的纵坐标。</param>
    /// <param name="scale_x">横向缩放倍数;<c>null</c> 表示未指定,取 <paramref name="scale_y"/> 或 1。</param>
    /// <param name="scale_y">纵向缩放倍数;<c>null</c> 表示未指定,取 <paramref name="scale_x"/> 或 1。</param>
    /// <param name="opacity">不透明度,取值 <c>[0, 1]</c>。</param>
    /// <remarks>
    /// <b>两个缩放参数的可空语义</b>:省略其一即「等比」—— 只给 <c>scale_x</c> 时 <c>scale_y</c> 取同值,
    /// 反之亦然;两者都省略即 1(原尺寸)。这比「省略即 1」更贴合实际用法:
    /// 想让一张图整体放大到 N 倍的人不会愿意写两遍同一个数,而写两遍正是漏改一处的经典来源。
    /// 需要单向拉伸时两个都显式给出即可。
    /// </remarks>
    [McpServerTool(
        Name = "image_composite",
        Title = "合成图像",
        Destructive = true,
        OpenWorld = false)]
    [Description(
        "把一个 Id 的图像贴到另一个 Id 的图像的指定位置上,支持缩放与半透明叠加:"
        + "源图左上角落在目标图 (x, y) 处,按 scale_x / scale_y 最近邻缩放,按 opacity 叠加。"
        + "省略 scale_x 或 scale_y 之一即等比(取另一个的值),两者都省略即 1(原尺寸)。"
        + CompositeSameIdNote + CompositeClipNote + CompositeOpacityNote + CompositeRetryNote
        + "成功返回 {ok,target_id,source_id,covered}。")]
    public CallToolResult Composite(
        [Description("目标图像 id(image_create 或 image_upload 返回),会被就地修改")]
        string? target_id,
        [Description("源图像 id,要与 target_id 不同;相同时按「先快照再合成」处理")]
        string? source_id,
        [Description("源图左上角在目标图中的横坐标,允许亚像素,默认 0")]
        double x = 0,
        [Description("源图左上角在目标图中的纵坐标,允许亚像素,默认 0")]
        double y = 0,
        [Description("横向缩放倍数,须大于 0;省略则取 scale_y 的值,两者都省略即 1")]
        double? scale_x = null,
        [Description("纵向缩放倍数,须大于 0;省略则取 scale_x 的值,两者都省略即 1")]
        double? scale_y = null,
        [Description("不透明度,取值 0 到 1(含端点),默认 1")]
        double opacity = 1)
    {
        if (string.IsNullOrEmpty(target_id))
        {
            return Error(CodeImageNotFound, $"target_id 不能为空(实际 {(target_id is null ? "null" : "空串")})。");
        }

        if (string.IsNullOrEmpty(source_id))
        {
            return Error(CodeImageNotFound, $"source_id 不能为空(实际 {(source_id is null ? "null" : "空串")})。");
        }

        // 可空合并的先后即语义:先看自己的值,再看对方的值,最后才是 1
        double effectiveScaleX = scale_x ?? scale_y ?? 1;
        double effectiveScaleY = scale_y ?? scale_x ?? 1;

        var style = new CompositeStyle(x, y, effectiveScaleX, effectiveScaleY, opacity);
        return ExecuteComposite(target_id, source_id, style);
    }

    // —————————————————————— 执行与映射 ——————————————————————


    /// <summary>
    /// 统一的绘制执行:校验 Id、委托注册表在条目锁内绘制、把全部失败形态映射为对外错误码。
    /// </summary>
    /// <remarks>
    /// <b>异常类型即错误码</b>:映射表由 <c>catch</c> 子句直接表达,不需要另建一张
    /// 「枚举值 → 错误码」的对照表 —— 那张表一旦漏掉一项,表现是某个错误被归到了错误的码上,
    /// 而调用方据此做出的处置也就跟着错了。
    /// </remarks>
    private CallToolResult Execute(string? id, Shape shape, DrawStyle style)
    {
        if (string.IsNullOrEmpty(id))
        {
            return Error(CodeImageNotFound, $"id 不能为空(实际 {(id is null ? "null" : "空串")})。");
        }

        try
        {
            int covered = store.Draw(id, shape, style);
            return Ok(new DrawResult(Ok: true, Id: id, Covered: covered));
        }
        catch (ImageNotFoundException ex)
        {
            return Error(CodeImageNotFound, ex.Message);
        }
        catch (InvalidStrokeWidthException ex)
        {
            return Error(CodeInvalidStrokeWidth, ex.Message);
        }
        catch (InvalidFillRuleException ex)
        {
            return Error(CodeInvalidFillRule, ex.Message);
        }
        catch (InvalidGeometryException ex)
        {
            return Error(CodeInvalidGeometry, ex.Message);
        }
        catch (PathSyntaxException ex)
        {
            return Error(CodeInvalidPath, ex.Message);
        }
        catch (DrawingLimitExceededException ex)
        {
            return Error(CodeLimitExceeded, ex.Message);
        }
    }

    /// <summary>
    /// 合成的执行与映射:委托注册表在<b>两个</b>条目锁内完成,把全部失败形态映射为对外错误码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="Execute"/> 分开而非合并,是因为两者的入参形态与可失败集合都不同:
    /// 合成没有形状、没有线宽、没有填充规则,也就永远不会抛
    /// <c>InvalidStrokeWidthException</c> / <c>InvalidFillRuleException</c> /
    /// <c>PathSyntaxException</c> / <c>DrawingLimitExceededException</c>。
    /// 把两套 <c>catch</c> 合成一个大的,会让合成路径上出现永远不可能命中的分支 ——
    /// 而「永远不命中」的分支一旦因日后改动变得可命中,是没有人会去看的。
    /// </para>
    /// <para>
    /// <b>Id 为空在此处<b>不再</b>判</b>:合成有两个 Id,必须分别指出是哪一个为空,
    /// 故该判定在工具方法体内完成(见 <c>Composite</c>),此处只处理注册表抛出的失败。
    /// </para>
    /// </remarks>
    private CallToolResult ExecuteComposite(string targetId, string sourceId, CompositeStyle style)
    {
        try
        {
            int covered = store.Composite(targetId, sourceId, style);
            return Ok(new CompositeResult(Ok: true, TargetId: targetId, SourceId: sourceId, Covered: covered));
        }
        catch (ImageNotFoundException ex)
        {
            return Error(CodeImageNotFound, CompositeNotFoundMessage(ex, targetId, sourceId));
        }
        catch (InvalidScaleException ex)
        {
            return Error(CodeInvalidScale, ex.Message);
        }
        catch (InvalidOpacityException ex)
        {
            return Error(CodeInvalidOpacity, ex.Message);
        }
        catch (InvalidGeometryException ex)
        {
            return Error(CodeInvalidGeometry, ex.Message);
        }
    }

    /// <summary>
    /// 构造合成失败的消息,<b>点名是哪个 Id 不可用</b>。
    /// </summary>
    /// <remarks>
    /// 注册表抛出的消息里只有一个 Id,而合成有两个入参 —— 直接透传会让调用方无从判断该重建哪一张。
    /// 两个 Id 相同时单独成句:此时「哪一个不可用」是个伪问题,而调用方多半是没意识到自己传了同一个值。
    /// </remarks>
    private static string CompositeNotFoundMessage(ImageNotFoundException ex, string targetId, string sourceId)
    {
        string field = string.Equals(targetId, sourceId, StringComparison.Ordinal)
            ? "target_id 与 source_id 指向同一个 Id,且它"
            : string.Equals(ex.Id, sourceId, StringComparison.Ordinal)
                ? "source_id"
                : "target_id";

        return $"{field} 不可用(target_id={Describe(targetId)}、source_id={Describe(sourceId)})。{ex.Message}";
    }

    // —————————————————————— 入参装配 ——————————————————————

    /// <summary>
    /// 校验颜色、装配绘制样式;任一项非法时返回对外的失败结果(否则返回 <c>null</c>)。
    /// </summary>
    /// <remarks>
    /// 颜色与填充规则<b>在此处</b>解析,而不是交给 <see cref="DrawStyle.Validate"/>:
    /// 后者只见得到已归一化的 <see cref="FillRule"/>,拿不到「用户实际传了什么串」,
    /// 而错误消息必须含实际值。
    /// </remarks>
    private static CallToolResult? BuildStyle(
        string? color,
        double stroke_width,
        bool stroke,
        bool fill,
        FillRule fill_rule,
        bool antialias,
        out DrawStyle style)
    {
        style = default;

        if (!ColorText.TryParseColor(color, out var parsedColor))
        {
            return Error(
                CodeInvalidColor,
                $"color 非法:期望 #RRGGBB 或 #RRGGBBAA(大小写不敏感、必须带 #),实际 {Describe(color)}。");
        }

        style = new DrawStyle(parsedColor, stroke_width, stroke, fill, fill_rule, antialias);
        return null;
    }

    /// <summary>
    /// 把六分量入参装配为仿射矩阵;分量个数不对时返回对外的失败结果(否则返回 <c>null</c>)。
    /// </summary>
    /// <remarks>
    /// 只在此处判<b>个数</b>,不判各分量的取值:后者是 <see cref="AffineTransform.Validate"/> 的职责,
    /// 两边都判必然逐渐分叉。个数则必须在此判 —— 少一个分量根本无法构造出矩阵,
    /// 而若默认补 <c>0</c>,一个「只想平移却少写一位」的调用方会得到一条退化成点的文本且无任何提示。
    /// </remarks>
    private static CallToolResult? BuildTransform(IReadOnlyList<double>? values, out AffineTransform transform)
    {
        transform = AffineTransform.Identity;

        if (values is null)
        {
            return null;
        }

        if (values.Count != TransformComponentCount)
        {
            return Error(
                CodeInvalidGeometry,
                $"transform 需要 {TransformComponentCount} 个分量,"
                + $"顺序同 SVG matrix(a,b,c,d,e,f)(x'=a·x+c·y+e、y'=b·x+d·y+f),实际 {values.Count} 个。");
        }

        transform = new AffineTransform(values[0], values[1], values[2], values[3], values[4], values[5]);
        return null;
    }

    /// <summary>构造填充规则非法的失败结果。</summary>
    private static CallToolResult InvalidFillRule(string? name) => Error(
        CodeInvalidFillRule,
        $"fill_rule 非法:期望 nonzero 或 evenodd(大小写不敏感),实际 {Describe(name)}。");

    /// <summary>把工具入参的顶点序列转成几何层的点(坐标为 double,允许亚像素)。</summary>
    private static PointD[] ToPoints(IReadOnlyList<ShapePoint> points)
    {
        var result = new PointD[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            result[i] = new PointD(points[i].X, points[i].Y);
        }

        // 顶点数下限(直线 ≥2、多边形 ≥3)不在工具层重复校验:那是形状自己的校验职责,
        // 在此再判一次必然与形状校验逐渐分叉,而分叉的那天没有告警
        return result;
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
}

// —————————————————————— 对外 JSON 契约 ——————————————————————

/// <summary>
/// 绘制工具的单个顶点参数:<c>{ "x", "y" }</c>,坐标为 <c>double</c>(允许亚像素)。
/// </summary>
/// <remarks>
/// 与 <see cref="PixelPoint"/> 的差别只在类型:像素写入是整数坐标,而几何顶点是实数坐标 ——
/// 抗锯齿正是「几何落在像素中心之间」这一情形下的产物,若沿用整数坐标,AA 就只剩整数端点上的
/// 覆盖率,形同虚设。
/// </remarks>
/// <param name="X">横坐标。</param>
/// <param name="Y">纵坐标。</param>
public sealed record ShapePoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

/// <summary>六项 <c>image_draw_*</c> 工具共用的成功结果:<c>{ ok, id, covered }</c>。</summary>
/// <param name="Ok">恒为 <c>true</c>。</param>
/// <param name="Id">目标标识。</param>
/// <param name="Covered">
/// 被覆盖(覆盖率大于 0)的像素数;形状整体落在画布外时为 <c>0</c>,且此时仍返回成功 ——
/// 「画到画布外」是裁剪语义,不是失败。
/// </param>
public sealed record DrawResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("covered")] int Covered);

/// <summary>
/// <c>image_composite</c> 的成功结果:<c>{ ok, target_id, source_id, covered }</c>。
/// </summary>
/// <remarks>
/// <b>不复用 <see cref="DrawResult"/></b>:后者只有一个 <c>id</c>,而合成必然涉及两个 ——
/// 若沿用单个 <c>id</c>,调用方无从知道它指的是目标还是源。多出的一个字段
/// 换来的是「结果里的每个 Id 都有明确归属」,值得让两个记录并存。
/// </remarks>
/// <param name="Ok">恒为 <c>true</c>。</param>
/// <param name="TargetId">目标标识,即被就地修改的那张。</param>
/// <param name="SourceId">源标识,即被贴上去的那张;与 <paramref name="TargetId"/> 相同时为自合并。</param>
/// <param name="Covered">
/// 被覆盖(覆盖率大于 0)的目标像素数;源图整体落在画布外、
/// 缩放后不足一个像素、<c>opacity</c> 为 0 或源图全透明时为 <c>0</c>,且此时仍返回成功 ——
/// 「贴到画布外」是裁剪语义,不是失败。
/// </param>
public sealed record CompositeResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("covered")] int Covered);

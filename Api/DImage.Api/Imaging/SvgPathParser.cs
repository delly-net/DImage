using System.Globalization;

namespace DImage.Api.Imaging;

// —————————————————————— 路径命令(解析产物) ——————————————————————
//
// 这些类型是「解析」与「扁平化」之间的中间表示:解析器只负责把文本变成「绝对坐标 + 命令语义」,
// 不含任何几何推导(曲线细分、平滑命令的隐含控制点、椭圆弧参数化)——
// 后者全部属于 PathFlattener。二者分开的理由是它们正交:语法错误的定位需要字符偏移,
// 而几何错误的定位需要点位,混在一处会让两类错误消息都说不清。

/// <summary>SVG path 的一条命令(绝对坐标)。</summary>
internal abstract record SvgPathCommand;

/// <summary>移动笔位,开启一条新的子路径。</summary>
/// <param name="Target">目标点(绝对坐标)。</param>
internal sealed record SvgMoveTo(PointD Target) : SvgPathCommand;

/// <summary>直线到目标点(<c>L</c> / <c>H</c> / <c>V</c> 均归入此命令)。</summary>
/// <param name="Target">目标点(绝对坐标)。</param>
internal sealed record SvgLineTo(PointD Target) : SvgPathCommand;

/// <summary>三次贝塞尔曲线(<c>C</c>)。</summary>
/// <param name="Control1">第一控制点。</param>
/// <param name="Control2">第二控制点。</param>
/// <param name="Target">终点。</param>
internal sealed record SvgCubicTo(PointD Control1, PointD Control2, PointD Target) : SvgPathCommand;

/// <summary>
/// 平滑三次贝塞尔曲线(<c>S</c>):只给出第二控制点,<b>第一控制点是前一曲线控制点的镜像</b>,
/// 需由 <see cref="PathFlattener"/> 结合上下文推导,故此处不预先补齐。
/// </summary>
/// <param name="Control2">第二控制点。</param>
/// <param name="Target">终点。</param>
internal sealed record SvgSmoothCubicTo(PointD Control2, PointD Target) : SvgPathCommand;

/// <summary>二次贝塞尔曲线(<c>Q</c>)。</summary>
/// <param name="Control">控制点。</param>
/// <param name="Target">终点。</param>
internal sealed record SvgQuadTo(PointD Control, PointD Target) : SvgPathCommand;

/// <summary>平滑二次贝塞尔曲线(<c>T</c>):控制点由前一曲线控制点镜像推导。</summary>
/// <param name="Target">终点。</param>
internal sealed record SvgSmoothQuadTo(PointD Target) : SvgPathCommand;

/// <summary>椭圆弧(<c>A</c>),按 SVG 规范的端点参数化记录。</summary>
/// <param name="Rx">椭圆横半轴。</param>
/// <param name="Ry">椭圆纵半轴。</param>
/// <param name="RotationDegrees">椭圆相对 x 轴的旋转角(度)。</param>
/// <param name="LargeArc">是否走大弧那一侧。</param>
/// <param name="Sweep">是否沿角度增大方向走。</param>
/// <param name="Target">弧的终点。</param>
internal sealed record SvgArcTo(
    double Rx,
    double Ry,
    double RotationDegrees,
    bool LargeArc,
    bool Sweep,
    PointD Target) : SvgPathCommand;

/// <summary>闭合当前子路径(<c>Z</c>)。</summary>
internal sealed record SvgClose : SvgPathCommand;

/// <summary>
/// SVG path 的 <c>d</c> 字符串解析器:把文本解析为绝对坐标的命令序列。
/// </summary>
/// <remarks>
/// <para>
/// <b>覆盖完整命令集</b>:<c>M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z</c>,含绝对与相对两种形态。
/// 相对坐标在此处即完成换算,故解析产物一律是绝对坐标 —— 下游无需再关心笔位从哪来。
/// </para>
/// <para>
/// <b>词法细节</b>(都是 SVG 规范里真实存在、且手写解析器最常漏掉的部分):
/// <list type="bullet">
///   <item><description>数字可带 <c>+</c>/<c>-</c> 前缀、可省略整数部分(<c>.5</c>)、可写科学计数法(<c>1e3</c>);</description></item>
///   <item><description>紧凑写法 <c>10.5.5</c> 等价于 <c>10.5 0.5</c> —— 第二个小数点即下一个数字的开头;</description></item>
///   <item><description>命令间与数字间允许空白与逗号分隔(此处对连续分隔符宽容处理,理由见 <see cref="SkipSeparators"/>);</description></item>
///   <item><description><b>隐式重复命令</b>:<c>M</c> 之后的数字对按 <c>L</c> 处理,<c>m</c> 之后按 <c>l</c>,其余命令可原样重复;</description></item>
///   <item><description><c>A</c> 命令的两个标志位是<b>单个字符</b>而非数字,允许与后续数字紧挨着写(<c>a5 5 0 0110 10</c>)。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>上限在解析过程中生效</b>:<c>d</c> 长度在入口处一次性校验,命令数则<b>每解析一条就复核一次</b>。
/// 若改成「先解析完、再检查命令数」,攻击者仍可让服务把 CPU 耗在解析上,上限就形同虚设。
/// </para>
/// <para>
/// <b>错误消息必须含字符偏移与出错片段</b>:一条几百字符的路径里,「语法错误」四个字没有任何定位价值,
/// 而调用方(MCP 客户端背后的语言模型)需要据此回改输入。
/// </para>
/// </remarks>
internal static class SvgPathParser
{
    /// <summary>解析 <c>d</c> 字符串。</summary>
    /// <param name="d">SVG path 的 <c>d</c> 字符串。</param>
    /// <returns>绝对坐标的命令序列。</returns>
    /// <exception cref="PathSyntaxException">字符串为空,或存在语法错误;异常消息含字符偏移与片段。</exception>
    /// <exception cref="DrawingLimitExceededException">长度或命令数超过 <see cref="DrawingLimits"/> 上限。</exception>
    /// <exception cref="InvalidGeometryException">坐标非有限值或超出量级上限。</exception>
    internal static List<SvgPathCommand> Parse(string? d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            throw new PathSyntaxException(
                $"path 的 d 不能为空:期望至少一条命令(例如 \"M0 0 L10 10\"),"
                + $"实际 {(d is null ? "null" : "空白字符串")}。");
        }

        // 长度在这里一次性挡住:它决定了后续一切解析工作量的上界,必须在任何扫描之前比较
        if (d.Length > DrawingLimits.MaxPathTextLength)
        {
            throw new DrawingLimitExceededException(
                $"path 的 d 长度 {d.Length} 超过上限 {DrawingLimits.MaxPathTextLength}。");
        }

        return new Cursor(d).ParseAll();
    }

    /// <summary>解析游标:承载文本、位置与笔位状态。</summary>
    /// <remarks>
    /// 用类而非一堆 <c>ref</c> 参数承载状态:笔位(<c>_current</c>)、子路径起点(<c>_subpathStart</c>)
    /// 与命令列表要在「读数字」和「执行命令」之间来回传递,做成引用参数会让每个辅助方法都长出四五个形参。
    /// </remarks>
    private sealed class Cursor(string text)
    {
        private static readonly char[] CommandLetters = "MmLlHhVvCcSsQqTtAaZz".ToCharArray();

        private readonly string _text = text;
        private readonly List<SvgPathCommand> _commands = [];

        private int _position;
        private PointD _current;
        private PointD _subpathStart;
        private bool _hasCurrent;

        /// <summary>解析整串。</summary>
        internal List<SvgPathCommand> ParseAll()
        {
            char implicitCommand = '\0';

            while (true)
            {
                SkipSeparators();
                if (_position >= _text.Length)
                {
                    break;
                }

                char command;
                if (IsCommandLetter(_text[_position]))
                {
                    command = _text[_position];
                    _position++;
                }
                else if (implicitCommand != '\0')
                {
                    // 走到这里说明上一轮末尾已确认「后面还跟着数字」,按同命令的隐式重复处理
                    command = implicitCommand;
                }
                else
                {
                    throw Syntax(_position, "期望命令字母(M/m/L/l/H/h/V/v/C/c/S/s/Q/q/T/t/A/a/Z/z)");
                }

                // SVG 规范要求路径以 moveto 起笔:没有起笔点,后续的 L/H/V 就没有「从哪里出发」可言
                if (_commands.Count == 0 && char.ToUpperInvariant(command) != 'M')
                {
                    throw Syntax(
                        _position,
                        $"第一条命令必须是 M 或 m(路径须以「移动到」起笔),实际 '{command}'");
                }

                if (_commands.Count >= DrawingLimits.MaxPathCommands)
                {
                    throw new DrawingLimitExceededException(
                        $"path 命令数超过上限 {DrawingLimits.MaxPathCommands}(已解析到位置 {_position})。");
                }

                char operation = char.ToUpperInvariant(command);
                bool relative = char.IsLower(command);

                Execute(operation, relative);

                SkipSeparators();

                // 只在此处判定「下一轮是否会隐式重复」:Z 不接受重复,命令字母与串尾同样终止重复。
                // 判定与消费分开写,是为了让「还跟着数字」这一事实只判定一次,避免两处判据逐渐分叉。
                implicitCommand = operation != 'Z'
                    && _position < _text.Length
                    && !IsCommandLetter(_text[_position])
                        ? SubstitutionFor(operation, relative)
                        : '\0';
            }

            return _commands;
        }

        /// <summary>求隐式重复时实际使用的命令字母:<c>M</c> 的后续数字对按 <c>L</c> 处理(相对形式按 <c>l</c>)。</summary>
        private static char SubstitutionFor(char operation, bool relative)
            => operation == 'M'
                ? (relative ? 'l' : 'L')
                : (relative ? char.ToLowerInvariant(operation) : operation);

        /// <summary>执行一条命令。</summary>
        /// <param name="operation">命令字母(已转大写)。</param>
        /// <param name="relative">是否为相对坐标形态。</param>
        private void Execute(char operation, bool relative)
        {
            switch (operation)
            {
                case 'M':
                {
                    PointD target = ReadPoint(relative);
                    _current = target;
                    _subpathStart = target;
                    _hasCurrent = true;
                    _commands.Add(new SvgMoveTo(target));
                    break;
                }

                case 'L':
                {
                    EnsureStarted("L");
                    PointD target = ReadPoint(relative);
                    _current = target;
                    _commands.Add(new SvgLineTo(target));
                    break;
                }

                case 'H':
                {
                    EnsureStarted("H");
                    double x = ReadNumber();
                    _current = new PointD(relative ? _current.X + x : x, _current.Y);
                    PathGeometry.ValidatePoint(_current, "H 命令的目标点");
                    _commands.Add(new SvgLineTo(_current));
                    break;
                }

                case 'V':
                {
                    EnsureStarted("V");
                    double y = ReadNumber();
                    _current = new PointD(_current.X, relative ? _current.Y + y : y);
                    PathGeometry.ValidatePoint(_current, "V 命令的目标点");
                    _commands.Add(new SvgLineTo(_current));
                    break;
                }

                case 'C':
                {
                    EnsureStarted("C");
                    // 同一命令内的所有相对坐标都以「命令开始时的笔位」为基准,
                    // 故三个点必须全部读完再更新 _current —— 边读边更新会让第二个点起基准就错了
                    PointD control1 = ReadPoint(relative);
                    PointD control2 = ReadPoint(relative);
                    PointD target = ReadPoint(relative);
                    _current = target;
                    _commands.Add(new SvgCubicTo(control1, control2, target));
                    break;
                }

                case 'S':
                {
                    EnsureStarted("S");
                    PointD control2 = ReadPoint(relative);
                    PointD target = ReadPoint(relative);
                    _current = target;
                    _commands.Add(new SvgSmoothCubicTo(control2, target));
                    break;
                }

                case 'Q':
                {
                    EnsureStarted("Q");
                    PointD control = ReadPoint(relative);
                    PointD target = ReadPoint(relative);
                    _current = target;
                    _commands.Add(new SvgQuadTo(control, target));
                    break;
                }

                case 'T':
                {
                    EnsureStarted("T");
                    PointD target = ReadPoint(relative);
                    _current = target;
                    _commands.Add(new SvgSmoothQuadTo(target));
                    break;
                }

                case 'A':
                {
                    EnsureStarted("A");

                    double rx = ReadNumber();
                    double ry = ReadNumber();
                    double rotation = ReadNumber();
                    bool largeArc = ReadFlag("large-arc-flag");
                    bool sweep = ReadFlag("sweep-flag");
                    PointD target = ReadPoint(relative);

                    // 半径按 SVG 规范取绝对值(负号被忽略),但量级仍须受限:
                    // 半径参与角度采样步长的计算,天文数字会直接放大成不可控的段数
                    PathGeometry.ValidateCoordinate(rx, "圆弧半径 rx");
                    PathGeometry.ValidateCoordinate(ry, "圆弧半径 ry");
                    PathGeometry.ValidateCoordinate(rotation, "圆弧旋转角");

                    _current = target;
                    _commands.Add(new SvgArcTo(rx, ry, rotation, largeArc, sweep, target));
                    break;
                }

                case 'Z':
                {
                    EnsureStarted("Z");
                    _commands.Add(new SvgClose());

                    // 闭合后笔位回到子路径起点:后续命令(若还有)从起点继续画新子路径
                    _current = _subpathStart;
                    break;
                }

                default:
                    throw Syntax(_position, $"未知的命令字母 '{operation}'");
            }
        }

        /// <summary>读取一个「坐标对」。</summary>
        /// <remarks>
        /// 相对坐标在<b>此层</b>换算为绝对坐标,下游一律只见绝对坐标。
        /// 首个命令若为相对 <c>m</c>,此时尚无当前点,按 SVG 规范<b>视作绝对坐标</b>处理。
        /// </remarks>
        private PointD ReadPoint(bool relative)
        {
            double x = ReadNumber();
            double y = ReadNumber();

            PointD point = relative && _hasCurrent
                ? new PointD(_current.X + x, _current.Y + y)
                : new PointD(x, y);

            PathGeometry.ValidatePoint(point, "path 坐标");
            return point;
        }

        /// <summary>读取一个数字词法单元。</summary>
        /// <remarks>
        /// 逐字符自行扫描而非按分隔符切分:SVG 允许数字紧挨着写(<c>10.5.5</c>、<c>1-2</c>),
        /// 任何「先切分再解析」的做法都会把 <c>1-2</c> 切成 <c>1-2</c> 这一个词元。
        /// </remarks>
        private double ReadNumber()
        {
            SkipSeparators();

            if (_position >= _text.Length)
            {
                throw Syntax(_position, "命令参数个数不足:期望一个数字,已到达字符串末尾");
            }

            int start = _position;
            int cursor = _position;

            if (cursor < _text.Length && (_text[cursor] == '+' || _text[cursor] == '-'))
            {
                cursor++;
            }

            int digits = 0;
            while (cursor < _text.Length && IsDigit(_text[cursor]))
            {
                cursor++;
                digits++;
            }

            if (cursor < _text.Length && _text[cursor] == '.')
            {
                cursor++;
                while (cursor < _text.Length && IsDigit(_text[cursor]))
                {
                    cursor++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                throw Syntax(start, "期望一个数字(可带正负号与小数点),但从该位置起读不出任何数字");
            }

            if (cursor < _text.Length && (_text[cursor] == 'e' || _text[cursor] == 'E'))
            {
                int exponent = cursor + 1;
                if (exponent < _text.Length && (_text[exponent] == '+' || _text[exponent] == '-'))
                {
                    exponent++;
                }

                int exponentDigits = 0;
                while (exponent < _text.Length && IsDigit(_text[exponent]))
                {
                    exponent++;
                    exponentDigits++;
                }

                if (exponentDigits == 0)
                {
                    throw Syntax(cursor, "科学计数法的指数部分缺少数字");
                }

                cursor = exponent;
            }

            string token = _text[start..cursor];

            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw Syntax(start, $"数字 \"{token}\" 无法解析为有效数值");
            }

            // .NET Core 3.0 起溢出会得到 ±Infinity 而非解析失败,必须在此显式转成语法错误 ——
            // 否则无穷大会一路流到包围盒,让循环边界失去约束
            if (double.IsInfinity(value) || double.IsNaN(value))
            {
                throw Syntax(start, $"数值 \"{token}\" 超出可表示范围");
            }

            _position = cursor;
            return value;
        }

        /// <summary>读取一个标志位,只接受单个字符 <c>0</c> 或 <c>1</c>。</summary>
        /// <remarks>
        /// 刻意<b>不</b>按数字解析:SVG 允许标志位与后续数字紧挨着写,<c>a5 5 0 0110 10</c> 中的
        /// <c>0110</c> 是两个标志 <c>0</c>、<c>1</c> 加数字 <c>10</c>。按数字读会把 <c>0110</c> 读成 110,
        /// 弧参数整体错位且完全不报错。
        /// </remarks>
        private bool ReadFlag(string name)
        {
            SkipSeparators();

            if (_position >= _text.Length)
            {
                throw Syntax(_position, $"命令参数个数不足:{name} 期望 0 或 1,已到达字符串末尾");
            }

            char value = _text[_position];
            switch (value)
            {
                case '0':
                    _position++;
                    return false;
                case '1':
                    _position++;
                    return true;
                default:
                    throw Syntax(_position, $"{name} 只能是单个字符 0 或 1,实际 '{value}'");
            }
        }

        /// <summary>跳过空白与逗号分隔符。</summary>
        /// <remarks>
        /// 连续的多个分隔符一并接受(<c>M0,,,0</c> 不报错)。SVG 规范对逗号的个数有限制,
        /// 但严格执行它并不能挡住任何真实缺陷 —— 多一个逗号不会让几何结果产生歧义,
        /// 而为此增加一条状态机规则,反而会让「逗号后跟命令字母」这类真正的错误路径更难测。
        /// 数字内部的空白(<c>#1 2345</c> 式的分裂词元)不受此影响:<see cref="ReadNumber"/> 自起始位置起
        /// 逐字符判定,空白会直接终止当前数字,绝不会被吞进数值里。
        /// </remarks>
        private void SkipSeparators()
        {
            while (_position < _text.Length && IsSeparator(_text[_position]))
            {
                _position++;
            }
        }

        /// <summary>确认已经出现过起笔命令。</summary>
        private void EnsureStarted(string operation)
        {
            if (!_hasCurrent)
            {
                throw Syntax(
                    _position,
                    $"{operation} 命令出现在起笔命令 M/m 之前:路径必须先从一次「移动到」开始");
            }
        }

        /// <summary>构造一条带字符偏移与出错片段的语法错误。</summary>
        private PathSyntaxException Syntax(int position, string reason)
        {
            int start = Math.Max(0, position - 12);
            int end = Math.Min(_text.Length, position + 12);

            string snippet = _text[start..end]
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');

            return new PathSyntaxException(
                $"SVG path 语法错误(位置 {position}):{reason}。"
                + $"出错片段:\"{snippet}\"(全串长度 {_text.Length})。");
        }

        private static bool IsCommandLetter(char value) => Array.IndexOf(CommandLetters, value) >= 0;

        private static bool IsDigit(char value) => value is >= '0' and <= '9';

        private static bool IsSeparator(char value)
            => value is ' ' or '\t' or '\r' or '\n' or '\f' or ',';
    }
}

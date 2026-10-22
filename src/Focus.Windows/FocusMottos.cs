using System.Windows.Input;

namespace DglabFocus.Windows;

public static class FocusMottos
{
    public static IReadOnlyList<string> Lines { get; } = Array.AsReadOnly<string>([
        "如此怠惰懒惰，我真要好好调教你了！",
        "小懒虫，书都翻开了，心怎么还在外面？",
        "又想摸鱼？把眼睛乖乖放回书上。",
        "才学一会儿就走神，你可真会考验我的耐心。",
        "别装忙，我可看得见你刚才在干什么。",
        "手指又不听话了？先把这一页读完。",
        "乖一点，今天的任务可还没完成。",
        "这么会偷懒，看来得让我认真盯着你了。",
        "你的小心思，都写在切换窗口的速度里了。",
        "想溜走？先把这道题交代清楚。",
        "书在等你，我也在等你收收心。",
        "别急着找借口，先把笔拿起来。",
        "嗯？这一页怎么半天都没动？",
        "刚才还说要努力，现在就开始耍赖了？",
        "把那些小把戏收起来，认真给我看。",
        "乖乖坐好，接下来该轮到知识进脑子了。",
        "眼睛别往我这儿瞟，题目还在等你。",
        "又被别的窗口勾走了？回来。",
        "你负责好好学，我负责盯紧你。",
        "偷懒的本事这么大，背书的时候怎么不拿出来？",
        "先别惦记休息，这一小段还没学完呢。",
        "今天可不会因为你撒娇，就少一道题。",
        "把注意力交回来，我陪你把这一段熬过去。",
        "再这么东张西望，我可要给你加一份监督了。",
        "小算盘打得挺响，书倒是一页没翻。",
        "都到这里了，别让我逮到你临阵开溜。",
        "认真一点，让我看看你真正的本事。",
        "你以为悄悄换个窗口，我就发现不了？",
        "先把这道题做出来，再来跟我讨价还价。",
        "这么爱分心，看来你很需要我陪着学。",
        "嘴上答应得好听，行动可得跟上。",
        "别拿无辜的眼神看我，先把注意力捡回来。",
        "这一段学完再说，别急着跟我讲条件。",
        "眼神飘到哪里去了？你的书在这边。",
        "今天的你，要比昨天更听话一点。",
        "心又跑远了？我给你领回来。",
        "想偷个懒，还偏偏挑在我眼皮底下。",
        "你的小聪明，拿来解题该有多好。",
        "把鼠标放稳，别总往别处点。",
        "别磨蹭，让我看看你认真起来的样子。",
        "这副心不在焉的样子，可糊弄不了我。",
        "答应自己的事，今天得乖乖做到。",
        "小懒虫醒醒，该和这一页较较劲了。",
        "我都替你盯着时间了，你就专心一点。",
        "现在想跑？刚才的雄心壮志去哪儿了？",
        "这点诱惑就让你走神，定力还得好好练。",
        "把脑袋里的小剧场关一关，该上课了。",
        "别把认真留到明天，现在就给我一点。",
        "我数着呢，你可别拿发呆冒充复习。",
        "先把这一关过了，再来摆出得意的小表情。",
        "今天也由我盯着，你只管乖乖学。"
    ]);

    public static int Pick(int? previous)
    {
        if (previous is not { } index || index < 0 || index >= Lines.Count) return Random.Shared.Next(Lines.Count);
        var next = Random.Shared.Next(Lines.Count - 1);
        return next >= index ? next + 1 : next;
    }
}

public partial class MainWindow
{
    private int focusMottoIndex;
    private void RotateFocusMotto()
    {
        focusMottoIndex = FocusMottos.Pick(focusMottoIndex);
        settings.LastMottoIndex = focusMottoIndex;
        if (page == "FocusPage") PageSubtitle.Text = FocusMottos.Lines[focusMottoIndex];
    }
    private void MottoPointerClick(object sender, MouseButtonEventArgs e) => RotateFocusMotto();
    private void MottoKeyDown(object sender, KeyEventArgs e)
    {
        if (page == "FocusPage" && e.Key is Key.Enter or Key.Space) { RotateFocusMotto(); e.Handled = true; }
    }
}

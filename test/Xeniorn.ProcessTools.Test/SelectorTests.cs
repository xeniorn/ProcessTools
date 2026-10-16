using Xeniorn.ProcessTools.Selection;

namespace Xeniorn.ProcessTools.Test;

public class SelectorTests
{
    private static (FakePlatform Platform, ProcessSelectionContext Context) Machine()
    {
        var p = new FakePlatform();
        p.Add(1, "init", parent: 0, path: "/sbin/init");
        p.Add(100, "Spectronaut", parent: 1, path: @"C:\Program Files\Biognosys\Spectronaut\Spectronaut.exe");
        p.Add(101, "presta", parent: 100, path: @"C:\Program Files\Biognosys\Spectronaut\presta.exe");
        p.Add(102, "presta", parent: 101, path: @"C:\Program Files\Biognosys\Spectronaut\presta.exe");
        p.Add(200, "notepad", parent: 1, path: @"C:\Windows\notepad.exe");
        p.Add(300, "svchost", parent: 1, path: @"C:\Windows\System32\svchost.exe");
        p.Add(400, "locked", parent: 1, path: null);
        p.Services["MyService"] = [300];
        return (p, new ProcessSelectionContext(p.ListProcesses(), p));
    }

    private static int[] Select(ProcessSelector selector)
    {
        var (_, ctx) = Machine();
        return ctx.Processes.Where(x => selector.Matches(x, ctx)).Select(x => x.ProcessId).Order().ToArray();
    }

    [Fact]
    public void By_pid() => Assert.Equal([200], Select(ProcessSelector.Pid(200)));

    [Theory]
    [InlineData("Spectronaut")]
    [InlineData("spectronaut.exe")]
    [InlineData("Spectro*")]
    [InlineData("S?ectronaut")]
    public void By_name_ignores_case_extension_and_supports_wildcards(string pattern) =>
        Assert.Equal([100], Select(ProcessSelector.Name(pattern)));

    [Fact]
    public void By_path_accepts_either_slash_and_never_matches_unreadable_paths() =>
        Assert.Equal([100, 101, 102], Select(ProcessSelector.Path("C:/Program Files/Biognosys/*")));

    [Fact]
    public void By_service_resolves_through_the_platform() =>
        Assert.Equal([300], Select(ProcessSelector.Service("My*")));

    [Fact]
    public void Descendants_include_grandchildren_but_not_the_root_by_default() =>
        Assert.Equal([101, 102], Select(ProcessSelector.DescendantsOf(ProcessSelector.Name("Spectronaut"))));

    [Fact]
    public void Descendants_can_include_the_root() =>
        Assert.Equal([100, 101, 102], Select(ProcessSelector.DescendantsOf(ProcessSelector.Name("Spectronaut"), includeRoot: true)));

    [Fact]
    public void Children_are_direct_only() =>
        Assert.Equal([101], Select(ProcessSelector.ChildrenOf(ProcessSelector.Name("Spectronaut"))));

    [Fact]
    public void Operators_combine_selectors() =>
        Assert.Equal([100, 200], Select(ProcessSelector.Name("Spectronaut") | ProcessSelector.Name("notepad")));

    [Fact]
    public void And_and_not_exclude() =>
        Assert.Equal([100], Select(ProcessSelector.Path("C:/Program Files/*") & !ProcessSelector.Name("presta")));

    [Fact]
    public void A_parent_cycle_does_not_hang()
    {
        var p = new FakePlatform();
        p.Add(1, "a", parent: 2);
        p.Add(2, "b", parent: 1);
        var ctx = new ProcessSelectionContext(p.ListProcesses(), p);
        var selector = ProcessSelector.DescendantsOf(ProcessSelector.Name("zzz"));
        Assert.DoesNotContain(ctx.Processes, x => selector.Matches(x, ctx));
    }

    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("a*c", "abbbc", true)]
    [InlineData("a*c", "abbbd", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("", "", true)]
    [InlineData("*x*", "abxcd", true)]
    [InlineData("*x*", "abcd", false)]
    public void Glob_matches(string pattern, string text, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, text));
}

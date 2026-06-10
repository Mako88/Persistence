using Persistence.Services.Container;

namespace Persistence.Tests.Container;

public class ShellCommandParserTests
{
    [Fact]
    public void SingleCommandYieldsItsProgram() =>
        Assert.Equal(["web_search"], ShellCommandParser.ExtractProgramNames("web_search \"rust async\""));

    [Fact]
    public void ChainedCommandsYieldEachProgram() =>
        Assert.Equal(["cd", "ls"], ShellCommandParser.ExtractProgramNames("cd docs && ls"));

    [Fact]
    public void PipedCommandsYieldEachProgram() =>
        Assert.Equal(["cat", "grep"], ShellCommandParser.ExtractProgramNames("cat notes.txt | grep TODO"));

    [Fact]
    public void SemicolonAndBackgroundSeparateSegments() =>
        Assert.Equal(["ls", "pwd"], ShellCommandParser.ExtractProgramNames("ls ; pwd"));

    [Fact]
    public void OperatorsInsideQuotesDoNotSplit() =>
        // The '&&' is part of the quoted argument, not a chain — one program.
        Assert.Equal(["web_search"], ShellCommandParser.ExtractProgramNames("web_search \"a && b\""));

    [Fact]
    public void RedirectsAreNotSegmentBoundaries() =>
        // '>' is not a chain/pipe operator — the whole thing is one 'echo' segment.
        Assert.Equal(["echo"], ShellCommandParser.ExtractProgramNames("echo hello > out.txt"));

    [Fact]
    public void FlagsAndArgumentsDoNotAffectTheProgramName() =>
        Assert.Equal(["python"], ShellCommandParser.ExtractProgramNames("python -c \"import os; print(os.getcwd())\""));

    [Fact]
    public void EmptySegmentsAreSkipped() =>
        Assert.Equal(["ls"], ShellCommandParser.ExtractProgramNames("ls ; ; "));
}

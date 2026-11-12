namespace Orchestrarr.Runner.Test;

public sealed class AgentTranscriptTests
{
    [Fact]
    public void What_the_agent_thinks_says_and_uses_becomes_lines_a_person_reads_with_the_path_of_the_repository()
    {
        var lines = AgentTranscript.Describe("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Maybe the route."},{"type":"text","text":"I will look."},{"type":"tool_use","name":"Grep","input":{"pattern":"app.get"}},{"type":"tool_use","name":"Glob","input":{"pattern":"**/*.js"}},{"type":"tool_use","name":"Write","input":{"file_path":"/work/a.js"}},{"type":"tool_use","name":"WebFetch","input":{}}]}}""");

        lines.Should().Equal("thinks: Maybe the route.", "says: I will look.", "searches app.get", "looks for files **/*.js", "writes a.js", "uses WebFetch");
    }

    [Fact]
    public void A_result_of_a_tool_is_cut_marked_when_it_is_an_error_and_read_from_a_list_of_parts()
    {
        var text = new string('x', 500);

        var cut = AgentTranscript.Describe("""{"type":"user","message":{"content":[{"type":"tool_result","content":"TEXT","is_error":true}]}}""".Replace("TEXT", text, StringComparison.Ordinal));
        var parts = AgentTranscript.Describe("""{"type":"user","message":{"content":[{"type":"tool_result","content":[{"type":"text","text":"line one"},{"type":"text","text":"line two"}]}]}}""");

        cut.Single().Should().StartWith("got an error: xxx").And.EndWith("…").And.HaveLength("got an error: ".Length + 301);
        parts.Single().Should().Be("got: line one\nline two");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{broken")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"assistant","message":{"content":"a string"}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"  "}]}}""")]
    public void A_line_that_tells_nothing_or_is_not_understood_gives_no_line(string line)
    {
        AgentTranscript.Describe(line).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":5}]}}""")]
    [InlineData("""{"type":"assistant","message":{"content":["a string", 5, null]}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":5,"input":"x"}]}}""")]
    [InlineData("""{"type":"user","message":{"content":[{"type":"tool_result","content":[5,"x"]}]}}""")]
    [InlineData("""{"type":"result","is_error":"no","result":5,"num_turns":{}}""")]
    public void A_shape_the_parser_does_not_know_never_throws_on_the_reader_thread(string line)
    {
        var describe = () => AgentTranscript.Describe(line);
        var result = () => AgentTranscript.Result(line);

        describe.Should().NotThrow();
        result.Should().NotThrow();
    }

    [Fact]
    public void The_result_gives_the_answer_the_turns_and_whether_it_is_an_error()
    {
        AgentTranscript.Result("""{"type":"result","is_error":false,"num_turns":4,"result":"Done."}""").Should().Be(("Done.", "4", false));
        AgentTranscript.Result("""{"type":"result","is_error":true,"num_turns":1}""").Should().Be((string.Empty, "1", true));
        AgentTranscript.Result("""{"type":"assistant"}""").Should().BeNull();
        AgentTranscript.Result("nope").Should().BeNull();
    }
}

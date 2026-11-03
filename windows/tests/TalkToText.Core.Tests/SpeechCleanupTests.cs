using TalkToText.Core;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>Taking hesitations out of dictation.</summary>
public sealed class SpeechCleanupTests
{
    [Theory]
    [InlineData("Éé, eu queria marcar a reunião.", "Eu queria marcar a reunião.")]
    [InlineData("Então, hã, a gente fecha na sexta.", "Então, a gente fecha na sexta.")]
    [InlineData("O orçamento, hum, ficou alto.", "O orçamento, ficou alto.")]
    [InlineData("Eu acho que que vai dar certo.", "Eu acho que vai dar certo.")]
    [InlineData("Eu eu vou ligar para eles.", "Eu vou ligar para eles.")]
    [InlineData("Manda o arquivo, ahn.", "Manda o arquivo.")]
    public void Hesitations_and_stutters_go(string said, string typed) => SpeechCleanup.Clean(said).ShouldBe(typed);

    [Theory]
    [InlineData("Isso é bom.")]
    [InlineData("É verdade, ele é o responsável.")]
    [InlineData("Foi muito muito bom.")]
    [InlineData("Humberto e Ana vão hoje.")]
    [InlineData("Ele chegou às 10h.")]
    public void Real_words_stay(string said) => SpeechCleanup.Clean(said).ShouldBe(said);

    [Fact]
    public void Text_that_is_only_a_hesitation_is_left_as_it_was() => SpeechCleanup.Clean("Hum.").ShouldBe("Hum.");
}

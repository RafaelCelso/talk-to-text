using TalkToText.App;
using TalkToText.Core;
using Shouldly;
using Xunit;

namespace TalkToText.AppTests;

/// <summary>What the pill and the Home page say while a dictation is being worked on.</summary>
public sealed class ActivityTextTests
{
    [Fact]
    public void Plain_transcription_still_reads_transcrevendo()
    {
        ActivityText.Describe(null).ShouldBe("Transcrevendo…");
        ActivityText.Describe(new DictationActivity(DictationStep.Transcribing)).ShouldBe("Transcrevendo…");
    }

    [Fact]
    public void A_translation_names_the_language_and_how_far_it_got() =>
        ActivityText.Describe(new DictationActivity(DictationStep.AiWriting, "Traduza para inglês.", Progress: 0.45, HasSelection: true))
            .ShouldBe("Traduzindo para o inglês… 45%");

    [Fact]
    public void Another_command_quotes_what_was_said() =>
        ActivityText.Describe(new DictationActivity(DictationStep.AiReading, "Deixa mais formal.", HasSelection: true))
            .ShouldBe("Aplicando “deixa mais formal”…");

    [Fact]
    public void A_long_instruction_is_cut_short() =>
        ActivityText.Describe(new DictationActivity(DictationStep.AiReading, "Escreve uma frase curta agradecendo o cliente pela parceria"))
            .ShouldBe("Executando “escreve uma frase curta agr…”…");

    [Fact]
    public void Polishing_says_it_is_revising() =>
        ActivityText.Describe(new DictationActivity(DictationStep.AiWriting, Polishing: true, Progress: 0.5))
            .ShouldBe("Revisando o texto… 50%");

    [Theory]
    [InlineData(DictationStep.LoadingSpeechModel, "Carregando o modelo de voz…")]
    [InlineData(DictationStep.ReadingSelection, "Lendo o texto selecionado…")]
    [InlineData(DictationStep.LoadingAi, "Preparando a IA…")]
    [InlineData(DictationStep.Typing, "Inserindo o texto…")]
    public void Every_step_has_its_own_line(DictationStep step, string text) =>
        ActivityText.Describe(new DictationActivity(step, "deixa mais formal", HasSelection: true)).ShouldBe(text);
}

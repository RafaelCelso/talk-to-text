using TalkToText.App;
using Shouldly;
using Xunit;

namespace TalkToText.AppTests;

/// <summary>Which spoken commands are translations, and what the AI is sent for them.</summary>
public sealed class CommandPromptTests
{
    [Theory]
    [InlineData("Traduza para inglês.", "English")]
    [InlineData("traduz pro ingles", "English")]
    [InlineData("Traduz isso pro espanhol", "Spanish")]
    [InlineData("Tradução para o francês, por favor.", "French")]
    [InlineData("passa isso pro alemão", "German")]
    [InlineData("Coloque em italiano.", "Italian")]
    public void Translations_are_recognised_with_their_language(string instruction, string language) =>
        CommandPrompt.TranslationTarget(instruction).ShouldBe(language);

    [Theory]
    [InlineData("Deixa mais formal.")]
    [InlineData("Resume em uma frase")]
    [InlineData("Corrige o português")]
    [InlineData("Traduz")]
    public void Other_commands_are_not_translations(string instruction) =>
        CommandPrompt.TranslationTarget(instruction).ShouldBeNull();

    [Fact]
    public void A_translation_sends_only_the_selection_and_names_the_language()
    {
        const string selection = "Me explique melhor como que funciona o 'Modo Comando'.";

        var request = CommandPrompt.Build("Traduza para inglês.", selection);

        request.User.ShouldBe(selection);
        request.System.ShouldContain("into English");
    }

    [Fact]
    public void Other_commands_carry_the_instruction_and_the_selection()
    {
        var request = CommandPrompt.Build("Deixa mais formal.", "oi, blz?");

        request.User.ShouldContain("Deixa mais formal.");
        request.User.ShouldContain("oi, blz?");
    }

    [Fact]
    public void Asking_for_a_translation_with_nothing_selected_writes_instead()
    {
        var request = CommandPrompt.Build("Traduz para inglês bom dia", null);

        request.User.ShouldStartWith("Instrução:");
    }
}

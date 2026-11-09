using Persistence.Store;

namespace Persistence.Tests;

/// <summary>Salience at the moment of storing: acknowledgements near zero, steering from the human above the default.</summary>
public sealed class SalienceTests
{
    [Theory]
    [InlineData("Sounds good!")]
    [InlineData("Sweet. Sounds good. Go for it.")]
    [InlineData("Thanks so much")]
    public void An_acknowledgement_is_near_zero(string said) => Assert.Equal(0.05f, Salience.Score(said, byHuman: true));

    [Fact]
    public void A_short_instruction_is_not_an_acknowledgement() =>
        Assert.True(Salience.Score("Yes, but don't touch the backups.", byHuman: true) > 0.2f);

    [Fact]
    public void A_human_decision_outranks_the_default() =>
        Assert.True(Salience.Score("Actually, I'd rather we keep the store public, and make sure nothing is ever hard-deleted. " +
                                   "What do you think of the hosted version?", byHuman: true) > Salience.Default + 0.2f);

    [Fact]
    public void The_peers_words_keep_the_default() =>
        Assert.Equal(Salience.Default, Salience.Score(new string('w', 200), byHuman: false));
}

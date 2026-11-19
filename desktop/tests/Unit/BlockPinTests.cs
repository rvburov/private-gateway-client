using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>PIN-код блока сайтов (решение 742): 4–8 цифр, хранится солью и хешем, сам код не записывается.</summary>
public sealed class BlockPinTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData("١٢٣٤", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pin_is_four_to_eight_digits(string? pin, bool valid) => BlockPin.IsValid(pin).ShouldBe(valid);

    [Fact]
    public void Only_the_same_code_matches_and_the_record_keeps_no_code()
    {
        var pin = BlockPin.Create("2468");
        pin.Matches("2468").ShouldBeTrue();
        pin.Matches("2469").ShouldBeFalse();
        pin.Matches("24680").ShouldBeFalse();
        pin.Matches(null).ShouldBeFalse();
        pin.ToString().ShouldNotContain("2468");
        BlockPin.Create("2468").ShouldNotBe(pin, "соль у каждого кода своя");
        BlockPin.Parse(pin.ToString()).ShouldBe(pin);
        BlockPin.Parse(pin.ToString())!.Matches("2468").ShouldBeTrue();
        BlockPin.Parse(null).ShouldBeNull();
        BlockPin.Parse(string.Empty).ShouldBeNull();
        BlockPin.Parse("broken").ShouldBeNull();
        new BlockPin("!!", "??").Matches("2468").ShouldBeFalse("испорченная запись не пускает и не падает");
        Should.Throw<ArgumentException>(() => BlockPin.Create("12"));
    }
}

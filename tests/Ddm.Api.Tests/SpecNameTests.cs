using Ddm.Api.Common;
using Ddm.Api.Specs;

namespace Ddm.Api.Tests;

public class SpecNameTests
{
    [Theory] [InlineData("pets")] [InlineData("payments.v2")] [InlineData("a_b-c")] [InlineData("0")]
    public void Valid(string name) => Assert.Equal(name, SpecName.Require(name));

    [Theory] [InlineData("")] [InlineData("Pets")] [InlineData("-x")] [InlineData(".x")] [InlineData("a/b")] [InlineData(null)]
    public void Invalid(string? name) => Assert.Equal("invalid_spec_name", Assert.Throws<ApiException>(() => SpecName.Require(name)).Code);

    [Fact] public void At_most_100_characters() => Assert.False(SpecName.IsValid(new string('a', 101)));
}

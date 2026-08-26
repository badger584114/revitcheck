using RevitCheck.Core.Checks;
using Xunit;

namespace RevitCheck.Core.Tests;

public class BearingMathTests
{
    [Fact]
    public void Due_north_is_zero_degrees()
    {
        Assert.Equal(0.0, BearingMath.AzimuthDegrees(0, 0, 0, 100), 6);
    }

    [Fact]
    public void Due_east_is_ninety_degrees()
    {
        Assert.Equal(90.0, BearingMath.AzimuthDegrees(0, 0, 100, 0), 6);
    }

    [Fact]
    public void Due_south_is_one_eighty_degrees()
    {
        Assert.Equal(180.0, BearingMath.AzimuthDegrees(0, 0, 0, -100), 6);
    }

    [Fact]
    public void Due_west_is_two_seventy_degrees()
    {
        Assert.Equal(270.0, BearingMath.AzimuthDegrees(0, 0, -100, 0), 6);
    }

    [Fact]
    public void Reciprocal_is_the_opposite_direction_wrapped_correctly()
    {
        Assert.Equal(180.0, BearingMath.Reciprocal(0.0), 6);
        Assert.Equal(0.0, BearingMath.Reciprocal(180.0), 6);
        Assert.Equal(170.0, BearingMath.Reciprocal(350.0), 6);
    }

    [Fact]
    public void Angular_difference_takes_the_short_way_around_the_wrap()
    {
        // 1deg and 359deg are 2deg apart, not 358deg.
        Assert.Equal(2.0, BearingMath.AngularDifference(1.0, 359.0), 6);
    }

    [Fact]
    public void Angular_difference_of_opposite_bearings_is_one_eighty()
    {
        Assert.Equal(180.0, BearingMath.AngularDifference(10.0, 190.0), 6);
    }

    [Fact]
    public void Angular_difference_is_symmetric_and_never_negative()
    {
        Assert.Equal(BearingMath.AngularDifference(30.0, 100.0), BearingMath.AngularDifference(100.0, 30.0), 9);
        Assert.True(BearingMath.AngularDifference(30.0, 100.0) >= 0);
    }
}

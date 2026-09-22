using QL.World.Models;
using QL.World.Services.Pbr;

namespace QL.World.Tests.Pbr;

public sealed class PbrRuleSetTests
{
    [Fact]
    public void Suffix_match_strips_and_maps_role()
    {
        (string, string)? result = PbrRuleSet.MatchRole("T_Wall_BaseColor", PbrRuleSet.Defaults);
        Assert.Equal(("T_Wall", PbrRoles.BaseColor), result);
    }

    [Fact]
    public void Match_is_case_insensitive_and_keeps_base_casing()
    {
        Assert.Equal(("T_Wall", PbrRoles.BaseColor),
            PbrRuleSet.MatchRole("T_Wall_BASECOLOR", PbrRuleSet.Defaults));
        Assert.Equal(("t_wall", PbrRoles.BaseColor),
            PbrRuleSet.MatchRole("t_wall_albedo", PbrRuleSet.Defaults));
    }

    [Fact]
    public void First_match_wins()
    {
        var rules = new List<PbrRule>
        {
            new("Normal", PbrRoles.Ao, true),
            new("_N", PbrRoles.Normal, true),
        };
        Assert.Equal(("T_X", PbrRoles.Ao), PbrRuleSet.MatchRole("T_X_Normal", rules));
    }

    [Fact]
    public void Boundary_rejects_word_prefix_but_allows_separator_pattern()
    {
        // "Abnormal" ends with "Normal" but the match follows a letter → rejected.
        Assert.Null(PbrRuleSet.MatchRole("Abnormal", PbrRuleSet.Defaults));
        // "Wall_D": "_D" starts with a separator, so the letter before is fine.
        Assert.Equal(("Wall", PbrRoles.BaseColor),
            PbrRuleSet.MatchRole("Wall_D", PbrRuleSet.Defaults));
        // "T_WallDisplacement": no separator before "Displacement" → rejected.
        Assert.Null(PbrRuleSet.MatchRole("T_WallDisplacement", PbrRuleSet.Defaults));
    }

    [Fact]
    public void Exact_name_equal_to_pattern_returns_null()
    {
        Assert.Null(PbrRuleSet.MatchRole("Normal", PbrRuleSet.Defaults));
    }

    [Fact]
    public void Disabled_and_blank_rules_are_skipped()
    {
        var rules = new List<PbrRule>
        {
            new("   ", PbrRoles.Ao, true),
            new("Normal", PbrRoles.Normal, false),
        };
        Assert.Null(PbrRuleSet.MatchRole("T_X_Normal", rules));
    }

    [Fact]
    public void Bare_name_without_any_suffix_returns_null()
    {
        Assert.Null(PbrRuleSet.MatchRole("T_Wall", PbrRuleSet.Defaults));
    }

    [Fact]
    public void Longer_patterns_win_over_shorter_prefixes()
    {
        // "Metallic" must map via its own rule, not fall through to "Metal".
        Assert.Equal(("T_X", PbrRoles.Metallic),
            PbrRuleSet.MatchRole("T_X_Metallic", PbrRuleSet.Defaults));
        Assert.Equal(("T_X", PbrRoles.Roughness),
            PbrRuleSet.MatchRole("T_X_Roughness", PbrRuleSet.Defaults));
    }
}

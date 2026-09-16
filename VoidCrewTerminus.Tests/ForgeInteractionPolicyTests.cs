using VoidCrewTerminus.Forge;
using Xunit;

namespace VoidCrewTerminus.Tests;

// No [Collection]: the policy is pure and writes no statics. It reads the cost curve through
// ForgeCostCurve, which falls back to the shipped default (1,1,2,2,3,3,4) under the test host.
public class ForgeInteractionPolicyTests
{
    private const int DefaultCapacity = 4;

    private static ForgeView Forge(
        bool hasModule = true, int level = ForgeCostCurve.MinLevel, bool hasViewId = true,
        int relics = 0, int capacity = DefaultCapacity, bool isAuthority = true) =>
        new(hasModule, level, hasViewId, relics, capacity, isAuthority);

    private static ForgeClick Click(
        ForgePayload payload, ForgeInteractableKind target,
        bool occupied = false, int carriedBoxLevel = ForgeCostCurve.MinLevel) =>
        new(payload, carriedBoxLevel, target, occupied);

    // A mismatch names the target that works rather than just refusing; the tubes and the
    // commit button are inches apart in-world.
    [Theory]
    [InlineData(ForgeInteractableKind.RelicTube)]
    [InlineData(ForgeInteractableKind.CommitButton)]
    [InlineData(ForgeInteractableKind.AlloyTerminal)]
    public void Module_box_on_the_wrong_target_points_at_the_socket(ForgeInteractableKind target)
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: false), Click(ForgePayload.ModuleBox, target));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("Place module boxes on the Forge's module socket.", d.Message);
    }

    [Fact]
    public void Module_box_is_refused_when_the_socket_is_already_full()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: true), Click(ForgePayload.ModuleBox, ForgeInteractableKind.ModuleSocket));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("The Forge already holds a module box.", d.Message);
    }

    // The level quoted is the carried box's: the socket reads 0 until the load lands.
    [Fact]
    public void Module_box_loads_and_reports_the_carried_box_level()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: false, level: 0),
            Click(ForgePayload.ModuleBox, ForgeInteractableKind.ModuleSocket, carriedBoxLevel: 5));

        Assert.Equal(ForgeAction.LoadModule, d.Action);
        Assert.Equal("Module loaded (L5). Insert relics and commit to upgrade.", d.Message);
    }

    [Theory]
    [InlineData(ForgeInteractableKind.ModuleSocket)]
    [InlineData(ForgeInteractableKind.CommitButton)]
    [InlineData(ForgeInteractableKind.AlloyTerminal)]
    public void Relic_on_the_wrong_target_points_at_the_tubes(ForgeInteractableKind target)
    {
        var d = ForgeInteractionPolicy.Decide(Forge(), Click(ForgePayload.Relic, target));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("Insert relics into the relic tubes.", d.Message);
    }

    [Fact]
    public void Relic_into_an_occupied_tube_is_refused()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(relics: 1), Click(ForgePayload.Relic, ForgeInteractableKind.RelicTube, occupied: true));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("That tube is occupied — pick an empty one.", d.Message);
    }

    [Fact]
    public void Relic_is_refused_when_the_Forge_is_at_capacity()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(relics: 2, capacity: 2), Click(ForgePayload.Relic, ForgeInteractableKind.RelicTube));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("The Forge is full (2/2 relics).", d.Message);
    }

    // Ordering matters: a full Forge whose clicked tube is also occupied must
    // still say "occupied". Reporting "full" would send the player away from a
    // Forge that would take the relic one tube over.
    [Fact]
    public void Occupied_tube_outranks_the_full_Forge_refusal()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(relics: 4, capacity: 4),
            Click(ForgePayload.Relic, ForgeInteractableKind.RelicTube, occupied: true));

        Assert.Equal("That tube is occupied — pick an empty one.", d.Message);
    }

    // Counts read as they will AFTER the insert, not as they are now.
    [Fact]
    public void Relic_insert_reports_the_post_insert_count()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(relics: 0), Click(ForgePayload.Relic, ForgeInteractableKind.RelicTube));

        Assert.Equal(ForgeAction.InsertRelic, d.Action);
        Assert.Contains("Relic inserted (1/4).", d.Message);
    }

    // Same for the projection: two relics on the default curve carry an L3 module to L5,
    // and the second insert must say so rather than quote the L4 of a moment ago.
    [Fact]
    public void Relic_insert_projects_from_the_post_insert_count()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(relics: 1, level: ForgeCostCurve.MinLevel),
            Click(ForgePayload.Relic, ForgeInteractableKind.RelicTube));

        Assert.Equal(ForgeAction.InsertRelic, d.Action);
        Assert.Equal("Relic inserted (2/4). Projected level: L5.", d.Message);
    }

    [Theory]
    [InlineData(ForgeInteractableKind.RelicTube)]
    [InlineData(ForgeInteractableKind.ModuleSocket)]
    [InlineData(ForgeInteractableKind.CommitButton)]
    [InlineData(ForgeInteractableKind.AlloyTerminal)]
    public void An_unaccepted_payload_is_refused_on_every_target(ForgeInteractableKind target)
    {
        var d = ForgeInteractionPolicy.Decide(Forge(), Click(ForgePayload.Other, target));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("The Forge only accepts relics and module boxes.", d.Message);
    }

    [Fact]
    public void Empty_handed_on_an_empty_socket_explains_how_to_fill_it()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: false), Click(ForgePayload.None, ForgeInteractableKind.ModuleSocket));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("Deconstruct a module and place its build box here to upgrade it.", d.Message);
    }

    // The spend depends on the ship's supplies and on who owns them, so the
    // terminal reports its own result and the policy says nothing.
    [Fact]
    public void Empty_handed_on_the_alloy_terminal_feeds_without_a_message()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(), Click(ForgePayload.None, ForgeInteractableKind.AlloyTerminal));

        Assert.Equal(ForgeAction.FeedAlloy, d.Action);
        Assert.Null(d.Message);
    }

    // Retrieval is the Forge's own action, not the player reaching past the machine's hull,
    // which occludes the ray to a deeply-socketed box.
    [Theory]
    [InlineData(ForgeInteractableKind.ModuleSocket)]
    [InlineData(ForgeInteractableKind.RelicTube)]
    public void Empty_handed_on_an_occupied_anchor_hands_the_item_back(ForgeInteractableKind target)
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: true, relics: 1), Click(ForgePayload.None, target, occupied: true));

        Assert.Equal(ForgeAction.RetrieveItem, d.Action);
        Assert.Null(d.Message);
    }

    // The retrieve check runs ahead of the per-target matrix, so the kinds that
    // never hold anything must fall through it rather than being swallowed — an
    // occupied commit lever still commits, an occupied terminal still feeds.
    [Theory]
    [InlineData(ForgeInteractableKind.CommitButton, ForgeAction.Commit)]
    [InlineData(ForgeInteractableKind.AlloyTerminal, ForgeAction.FeedAlloy)]
    public void An_occupied_flag_does_not_hijack_the_lever_or_the_terminal(
        ForgeInteractableKind target, ForgeAction expected)
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: true, relics: 1), Click(ForgePayload.None, target, occupied: true));

        Assert.Equal(expected, d.Action);
    }

    [Theory]
    [InlineData(ForgeInteractableKind.ModuleSocket, true)]
    [InlineData(ForgeInteractableKind.RelicTube, true)]
    [InlineData(ForgeInteractableKind.CommitButton, false)]
    [InlineData(ForgeInteractableKind.AlloyTerminal, false)]
    public void RetrievesWhenOccupied_is_the_single_source_for_the_prompt_and_the_click(
        ForgeInteractableKind kind, bool expected)
    {
        Assert.Equal(expected, ForgeInteractionPolicy.RetrievesWhenOccupied(kind));
    }

    // Pins which of the two sources decides. HasModule is semantic bookkeeping;
    // occupancy is what is physically pinned to the anchor, and only the latter can
    // answer "what do I hand back" — so only the latter may trigger a retrieve.
    [Fact]
    public void Empty_handed_on_an_empty_socket_explains_itself_even_when_HasModule_is_set()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: true), Click(ForgePayload.None, ForgeInteractableKind.ModuleSocket));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("Deconstruct a module and place its build box here to upgrade it.", d.Message);
    }

    [Fact]
    public void Empty_handed_on_a_tube_reads_out_the_loaded_Forge()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: true, level: 3, relics: 2),
            Click(ForgePayload.None, ForgeInteractableKind.RelicTube));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal("Forge: L3 module loaded, 2/4 relics, projected L5.", d.Message);
    }

    [Fact]
    public void Empty_handed_on_a_tube_reads_out_the_empty_Forge()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(hasModule: false, level: 0, relics: 0),
            Click(ForgePayload.None, ForgeInteractableKind.RelicTube));

        Assert.Equal("Forge: no module loaded, 0/4 relics.", d.Message);
    }

    // The three refusals delegate their wording to ForgeLabels rather than
    // carrying copies — asserting against DescribeCommit is the point, since a
    // literal here would be a fourth copy of the string.
    [Theory]
    [InlineData(false, true, 1, CommitStatus.NoModule)]
    [InlineData(true, false, 1, CommitStatus.MissingViewId)]
    [InlineData(true, true, 0, CommitStatus.NoRelics)]
    public void Commit_refusals_speak_through_ForgeLabels(
        bool hasModule, bool hasViewId, int relics, CommitStatus expected)
    {
        var forge = Forge(hasModule: hasModule, hasViewId: hasViewId, relics: relics);

        var d = ForgeInteractionPolicy.Decide(
            forge, Click(ForgePayload.None, ForgeInteractableKind.CommitButton));

        Assert.Equal(ForgeAction.None, d.Action);
        Assert.Equal(
            ForgeLabels.DescribeCommit(CommitOutcome.Failure(expected),
                forge.SocketedBoxLevel, forge.RelicCount)[0],
            d.Message);
    }

    // The invariant the split exists to protect: host and client must refuse identically.
    [Theory]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    public void Host_and_client_refuse_a_commit_identically(bool hasModule, bool hasViewId, int relics)
    {
        var click = Click(ForgePayload.None, ForgeInteractableKind.CommitButton);

        var host = ForgeInteractionPolicy.Decide(
            Forge(hasModule: hasModule, hasViewId: hasViewId, relics: relics, isAuthority: true), click);
        var client = ForgeInteractionPolicy.Decide(
            Forge(hasModule: hasModule, hasViewId: hasViewId, relics: relics, isAuthority: false), click);

        Assert.Equal(ForgeAction.None, host.Action);
        Assert.Equal(ForgeAction.None, client.Action);
        Assert.NotNull(host.Message);
        Assert.Equal(host.Message, client.Message);
    }

    // Solo counts as authority, so single-player commits inline and says nothing
    // up front — the outcome does the talking.
    [Fact]
    public void The_authority_commits_inline()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(relics: 1, isAuthority: true),
            Click(ForgePayload.None, ForgeInteractableKind.CommitButton));

        Assert.Equal(ForgeAction.Commit, d.Action);
        Assert.Null(d.Message);
    }

    // Cursed markers and RNG live on the host, so a client asks.
    [Fact]
    public void A_client_asks_the_host_to_commit()
    {
        var d = ForgeInteractionPolicy.Decide(
            Forge(relics: 1, isAuthority: false),
            Click(ForgePayload.None, ForgeInteractableKind.CommitButton));

        Assert.Equal(ForgeAction.RequestCommit, d.Action);
        Assert.Equal("Requesting upgrade from the host…", d.Message);
    }
}

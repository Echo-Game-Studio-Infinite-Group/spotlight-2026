using System;
using System.Collections.Generic;
using System.Linq;
using GameJam.Flow;
using GameJam.FlowEditor;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class FlowRuleTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private RunFlowConfig _config;
        private RoomPoolDefinition _neutral, _chaos;

        [SetUp]
        public void SetUp()
        {
            _config = Make<RunFlowConfig>("config");
            _neutral = Make<RoomPoolDefinition>("neutral"); _neutral.Id = "neutral";
            _neutral.Entries = Enumerable.Range(0, 3).Select(i => new RoomPoolEntry { Variant = Variant("neutral" + i), Weight = i == 0 ? 4f : 1f }).ToArray();
            _chaos = Make<RoomPoolDefinition>("chaos"); _chaos.Id = "chaos"; _chaos.ChangesChaos = true;
            _chaos.Entries = new[] { new RoomPoolEntry { Variant = Variant("chaos", 10) } };
            _config.InitialRoom = _neutral.Entries[0].Variant; _config.FinalRoom = Variant("final");
            _config.NormalPools = new[] { _neutral };
            _config.Loops = new[] { Loop("low", 0, 30), Loop("high", 80, 99) };
        }
        [TearDown]
        public void TearDown() { foreach (Object value in _objects) Object.DestroyImmediate(value); _objects.Clear(); }
        private T Make<T>(string name) where T : ScriptableObject
        { T value = ScriptableObject.CreateInstance<T>(); value.name = name; _objects.Add(value); return value; }
        private RoomVariantDefinition Variant(string id, int gain = 0)
        {
            var room = Make<RoomDefinition>(id + "room"); room.Id = id;
            var variant = Make<RoomVariantDefinition>(id); variant.Id = id; variant.Room = room; variant.StabilizerGain = gain;
            room.Variants = new[] { variant }; return variant;
        }
        private LoopRuleDefinition Loop(string id, int min, int max)
        {
            var loop = Make<LoopRuleDefinition>(id); loop.Id = id; loop.MinimumChaos = min; loop.MaximumChaos = max;
            loop.NeutralPool = _neutral; loop.ChaosPool = _chaos; return loop;
        }

        [TestCase(0, "low")]
        [TestCase(30, "low")]
        [TestCase(31, "")]
        [TestCase(79, "")]
        [TestCase(80, "high")]
        [TestCase(99, "high")]
        [TestCase(100, "")]
        public void ChaosBoundariesChooseCorrectRoute(int chaos, string expected)
        {
            var planner = new RoomSelectionPlanner(_config, 42);
            RoomVariantDefinition next = planner.Plan(chaos);
            Assert.That(next, Is.Not.Null, planner.LastError);
            Assert.That(planner.LoopId, Is.EqualTo(expected));
            Assert.That(next == _config.FinalRoom, Is.EqualTo(chaos == 100));
        }
        [Test]
        public void LoopContainsExactlyOneChaosSlotAndRepeatsWithoutRerolling()
        {
            var planner = new RoomSelectionPlanner(_config, 333);
            planner.Plan(20);
            RoomVariantDefinition[] cycle = planner.Cycle.ToArray();
            Assert.That(cycle.Length, Is.EqualTo(4).Or.EqualTo(7));
            Assert.That(cycle.Count(variant => variant.StabilizerGain > 0), Is.EqualTo(1));
            uint random = planner.Random.State;
            for (int i = 0; i < cycle.Length * 4; i++)
            {
                Assert.That(planner.Plan(20), Is.SameAs(cycle[i % cycle.Length]));
                planner.CommitSuccessor();
            }
            Assert.That(planner.Random.State, Is.EqualTo(random));
        }
        [Test]
        public void CrossingOutOfLoopClearsItsRoster()
        {
            var planner = new RoomSelectionPlanner(_config, 42);
            planner.Plan(30); planner.CommitSuccessor(); planner.Plan(31);
            Assert.That(planner.Cycle, Is.Empty); Assert.That(planner.CycleIndex, Is.Zero);
            planner.Plan(80); Assert.That(planner.LoopId, Is.EqualTo("high"));
        }
        [Test]
        public void RepeatedPlanningDoesNotAdvanceTheLoopSlot()
        {
            var planner = new RoomSelectionPlanner(_config, 42);
            RoomVariantDefinition next = planner.Plan(0);
            Assert.That(planner.Plan(0), Is.SameAs(next));
            Assert.That(planner.CycleIndex, Is.Zero);
        }
        [Test]
        public void CheckpointSnapshotRestoresTheRosterCursorVisitsAndRandomState()
        {
            var planner = new RoomSelectionPlanner(_config, 42);
            RoomVariantDefinition expected = planner.Plan(0);
            FlowPlannerSnapshot snapshot = planner.Capture(); uint random = planner.Random.State;
            planner.CommitSuccessor(); planner.RegisterEntry(expected); planner.Plan(50);
            planner.Restore(snapshot);
            Assert.That(planner.Plan(0), Is.SameAs(expected));
            Assert.That(planner.CycleIndex, Is.Zero); Assert.That(planner.VisitCount(expected), Is.Zero);
            Assert.That(planner.Random.State, Is.EqualTo(random));
        }
        [Test]
        public void ValidationDetectsAChaosPoolThatCannotAdvanceItsLoop()
        {
            _chaos.Entries[0].Variant.MinimumChaos = 20;
            Assert.That(FlowConfigValidator.Validate(_config).Any(error => error.Contains("无法组成含变化房的循环")), Is.True);
        }
        [Test]
        public void VisitedVariantWeightsDecayWithoutChangingOtherVariants()
        {
            var planner = new RoomSelectionPlanner(_config, 42);
            RoomPoolEntry entry = _neutral.Entries[0];
            planner.RegisterEntry(entry.Variant); planner.RegisterEntry(entry.Variant);
            Assert.That(planner.EffectiveWeight(entry), Is.EqualTo(.25).Within(1e-8));
            Assert.That(planner.EffectiveWeight(_neutral.Entries[1]), Is.EqualTo(1));
            _config.DecayOnlyOnce = true;
            Assert.That(planner.EffectiveWeight(entry), Is.EqualTo(1));
        }
        [Test]
        public void WeightedSamplingApproximatesConfiguredRatios()
        {
            var planner = new RoomSelectionPlanner(_config, 12345);
            int heavy = 0;
            for (int i = 0; i < 20000; i++) if (planner.Plan(50) == _neutral.Entries[0].Variant) heavy++;
            Assert.That(heavy / 20000.0, Is.InRange(.64, .69));
        }
        [Test]
        public void LongRunsDoNotLoseAllWeightsToFloatingPointUnderflow()
        {
            var planner = new RoomSelectionPlanner(_config, 1);
            foreach (RoomPoolEntry entry in _neutral.Entries)
                for (int i = 0; i < 2000; i++) planner.RegisterEntry(entry.Variant);
            Assert.That(planner.Plan(50), Is.Not.Null, planner.LastError);
        }
        [Test]
        public void SameSeedAndEventsReproduceTheSameRun()
        {
            var a = new RoomSelectionPlanner(_config, 867); var b = new RoomSelectionPlanner(_config, 867);
            for (int i = 0; i < 200; i++)
            {
                int chaos = new[] { 0, 30, 31, 50, 79, 80, 99, 100 }[i % 8];
                RoomVariantDefinition x = a.Plan(chaos), y = b.Plan(chaos);
                Assert.That(x, Is.SameAs(y)); a.CommitSuccessor(); b.CommitSuccessor(); a.RegisterEntry(x); b.RegisterEntry(y);
                Assert.That(a.Random.State, Is.EqualTo(b.Random.State));
            }
        }
        [Test]
        public void ImpossibleNonRepeatingCycleFailsWithoutConsumingRandomState()
        {
            _config.Loops[0].AllowRepeatedRooms = false; _config.Loops[0].NeutralCounts = new[] { 6 };
            var planner = new RoomSelectionPlanner(_config, 42); uint before = planner.Random.State;
            Assert.That(planner.Plan(0), Is.Null); Assert.That(planner.Cycle, Is.Empty);
            Assert.That(planner.Random.State, Is.EqualTo(before));
        }
        [Test]
        public void IncompatibleRoomsAreRemovedBeforeTheWeightedDraw()
        {
            var planner = new RoomSelectionPlanner(_config, 42);
            RoomVariantDefinition only = _neutral.Entries[2].Variant;
            for (int i = 0; i < 100; i++) Assert.That(planner.Plan(50, variant => variant == only), Is.SameAs(only));
        }
        [Test]
        public void OrdinaryDeathStopsAtZeroAndSpecialDeathIsExplicit()
        {
            var chaos = new ChaosService(_config); chaos.Add(5, "test"); chaos.ApplyDeath(true);
            Assert.That(chaos.Value, Is.Zero);
            _config.EnableSpecialRoutes = true; chaos.ApplyDeath(true); Assert.That(chaos.Value, Is.EqualTo(-10));
            Assert.That(chaos.TrySetSpecial(999, "test"), Is.True);
            _config.ExceptionalPool = _neutral; _neutral.Entries[0].Variant.MaximumChaos = 999;
            Assert.That(new RoomSelectionPlanner(_config, 4).Plan(999), Is.SameAs(_neutral.Entries[0].Variant));
        }
        [Test]
        public void VariantResolutionChangesContentWithoutChangingTheRoomIdentity()
        {
            RoomVariantDefinition low = _neutral.Entries[0].Variant; low.MaximumChaos = 30;
            var high = Make<RoomVariantDefinition>("sameRoomHigh"); high.Id = "sameRoomHigh"; high.Room = low.Room; high.MinimumChaos = 31;
            low.Room.Variants = new[] { low, high };
            Assert.That(low.Room.ResolveVariant(40, low), Is.SameAs(high));
            Assert.That(low.Room.ResolveVariant(20, high), Is.SameAs(low));
        }
        [Test]
        public void CsvRoundTripPreservesWeightsAndEnableFlags()
        {
            string csv = RoomPoolCsv.Export(_config);
            _neutral.Entries[0].Weight = 100;
            RoomPoolCsv.Import(_config, csv);
            Assert.That(_neutral.Entries[0].Weight, Is.EqualTo(4));
            Assert.That(_neutral.Entries.All(entry => entry.Enabled), Is.True);
        }
        [Test]
        public void InvalidCsvDoesNotPartiallyModifyAnyPool()
        {
            string csv = RoomPoolCsv.Export(_config);
            csv = csv.Replace(",4,1", ",2,1") + "neutral,missing,,1,1\n";
            Assert.Throws<FormatException>(() => RoomPoolCsv.Import(_config, csv));
            Assert.That(_neutral.Entries[0].Weight, Is.EqualTo(4));
        }
        [Test]
        public void CsvSupportsBomQuotedCommasAndMultilineFields()
        {
            List<string[]> parsed = RoomPoolCsv.Parse("\uFEFF\"a,b\",\"c\"\"d\",\"e\nf\"\r\n");
            Assert.That(parsed[0], Is.EqualTo(new[] { "a,b", "c\"d", "e\nf" }));
            Assert.Throws<FormatException>(() => RoomPoolCsv.Parse("\"unterminated"));
        }
    }
}

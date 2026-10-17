using System.Linq;
using GameJam.Flow;
using GameJam.FlowEditor;
using NUnit.Framework;
using UnityEditor;

namespace GameJam.FlowTests
{
    public sealed class FlowExampleTests
    {
        [Test]
        public void GeneratedWhiteboxConfigurationHasCompleteReferences()
        {
            RunFlowConfig config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>(FlowExampleSetup.ConfigPath);
            Assert.That(config, Is.Not.Null, "先运行“生成白盒配表示例”");
            Assert.That(FlowConfigValidator.Validate(config), Is.Empty);
            foreach (RoomDefinition room in FlowConfigAssets.Collect(config).OfType<RoomDefinition>())
            {
                Assert.That(room.Template.Navigation, Is.Not.Null, room.Id);
                Assert.That(room.Template.Entry.localPosition.sqrMagnitude, Is.LessThan(.0001f), room.Id);
                Assert.That(room.Template.GetComponentsInChildren<UnityEngine.MeshFilter>(true).Length, Is.GreaterThan(10), room.Id);
                Assert.That(room.Template.GetComponentsInChildren<MapSeamSurface>(true), Is.Empty);
            }
        }
        [Test]
        public void SampleCsvCanBeReimportedWithoutLosingItsAssetReferences()
        {
            RunFlowConfig config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>(FlowExampleSetup.ConfigPath);
            string csv = RoomPoolCsv.Export(config); RoomPoolCsv.Import(config, csv);
            Assert.That(RoomPoolCsv.Export(config), Is.EqualTo(csv));
            Assert.That(FlowConfigValidator.Validate(config), Is.Empty);
        }
    }
}

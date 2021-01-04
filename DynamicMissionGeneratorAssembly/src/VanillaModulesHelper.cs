using System.Collections.Generic;
using System.Linq;

namespace DynamicMissionGeneratorAssembly
{
	public static class VanillaModulesHelper
	{
		public static IEnumerable<string> VanillaSolvableModules = new string[] { "BigButton", "Maze", "Memory", "Morse", "Keypad", "Password", "Simon", "Venn", "WhosOnFirst", "WireSequence", "Wires" };

		public static IEnumerable<string> VanillaNeedyModules = new string[] { "NeedyCapacitor", "NeedyVentGas", "NeedyKnob" };

		public static Dictionary<string, KMComponentPool.ComponentTypeEnum> VanillaModuleNameToEnumMap = new Dictionary<string, KMComponentPool.ComponentTypeEnum>()
		{
			{ "BigButton", KMComponentPool.ComponentTypeEnum.BigButton },
			{ "Keypad", KMComponentPool.ComponentTypeEnum.Keypad},
			{ "Maze", KMComponentPool.ComponentTypeEnum.Maze },
			{ "Memory", KMComponentPool.ComponentTypeEnum.Memory},
			{ "Morse", KMComponentPool.ComponentTypeEnum.Morse },
			{ "Password", KMComponentPool.ComponentTypeEnum.Password},
			{ "Simon", KMComponentPool.ComponentTypeEnum.Simon },
			{ "Venn", KMComponentPool.ComponentTypeEnum.Venn },
			{ "WhosOnFirst", KMComponentPool.ComponentTypeEnum.WhosOnFirst},
			{ "WireSequence", KMComponentPool.ComponentTypeEnum.WireSequence },
			{ "Wires", KMComponentPool.ComponentTypeEnum.Wires },
			{ "NeedyCapacitor", KMComponentPool.ComponentTypeEnum.NeedyCapacitor},
			{ "NeedyKnob", KMComponentPool.ComponentTypeEnum.NeedyKnob },
			{ "NeedyVentGas", KMComponentPool.ComponentTypeEnum.NeedyVentGas }
		};
	}
}

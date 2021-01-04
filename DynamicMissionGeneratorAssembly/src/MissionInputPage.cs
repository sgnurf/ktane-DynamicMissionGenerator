﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using static DynamicMissionGeneratorAssembly.MissionsPage;

namespace DynamicMissionGeneratorAssembly
{
	public class MissionInputPage : MonoBehaviour
	{
		public KMSelectable SwitchButtonSelectable;
		public KMSelectable SaveButtonSelectable;
		public KMSelectable RunButtonSelectable;
		public RectTransform CanvasTransform;
		public InputField InputField;
		public KMGameCommands GameCommands;
		public ModuleListItem ModuleListItemPrefab;
		public RectTransform ModuleList;
		public Scrollbar Scrollbar;
		public RectTransform ScrollView;
		public ModuleListItem Tooltip;
		public Text ErrorPopup;
		public Text MissionText;
		public Prompt Prompt;
		public Alert Alert;
		public Confirmation Confirmation;

		private string missionName;
		private readonly List<GameObject> listItems = new List<GameObject>();
		private bool multipleBombsEnabled, factoryEnabled;

		public KMAudio Audio;
		public KMGameInfo GameInfo;

		private readonly List<ModuleData> moduleData = new List<ModuleData>();
		private static readonly Regex tokenRegex = new Regex(@"
			\G\s*()(?:  # Group 1 marks the position after whitespace; used for completion
				//.*|/\*[\s\S]*?(?:\*/|$)|  # Comment
				(?<Close>\))|
				(?:time:)?(?<Time1>\d{1,9}):(?<Time2>\d{1,9})(?::(?<Time3>\d{1,9}))?(?!\S)|
				(?<Strikes>\d{1,9})X(?!\S)|
				(?<Setting>strikes|needyactivationtime|widgets|nopacing|frontonly|factory|ruleseed)(?::(?<Value>[^\s)]*))?|
				(?:(?<Count>\d{1,9})(?<NoDuplicate>!)?\s*[;*]\s*)?
				(?:
					(?<Open>\()|
					(?<ID>(?:[^\s'"",+)]|(?<q>['""])(?:(?!\k<q>)[\s\S])*(?:\k<q>|(?<Error>))|[,+]\s*)+)  # Module pool; ',' or '+' may be followed by spaces; 'Error' group catches unclosed quotes
				)
			)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
		private readonly Dictionary<string, Profile> profiles = new Dictionary<string, Profile>();

		private int tabListIndex = -1;
		private int tabCursorPosition = -1;
		private string tabStub;
		private bool suppressTextChanged;
		private int repositionScrollView;
		private Vector2 oldMousePosition;
		private float hoverDelay;
		private string prevText = "";
		private int prevCursorPosition = -1;

		private static readonly ModuleData[] factoryModeList = new[]
		{
			new ModuleData("static", "Factory: Static"),
			new ModuleData("finite", "Factory: Finite"),
			new ModuleData("finitegtime", "Factory: Finite + global time"),
			new ModuleData("finitegstrikes", "Factory: Finite + global strikes"),
			new ModuleData("finitegtimestrikes", "Factory: Finite + global time and strikes"),
			new ModuleData("infinite", "Factory: Infinite"),
			new ModuleData("infinitegtime", "Factory: Infinite + global time"),
			new ModuleData("infinitegstrikes", "Factory: Infinite + global strikes"),
			new ModuleData("infinitegtimestrikes", "Factory: Infinite + global time and strikes")
		};

		private static FieldInfo cursorVertsField = typeof(InputField).GetField("m_CursorVerts", BindingFlags.NonPublic | BindingFlags.Instance);

		public void Start()
		{
			InputField.Scroll += InputField_Scroll;
			InputField.Submit += (sender, e) => RunInteract();
			InputField.TabPressed += InputField_TabPressed;

			if (!Application.isEditor)
			{
				DynamicMissionGenerator.Instance.InputPage = this;
				Action<string> goToPage = (Action<string>) DynamicMissionGenerator.ModSelectorApi["GoToPageMethod"];
				SwitchButtonSelectable.OnInteract += () => { goToPage("PageTwo"); return false; };
				SaveButtonSelectable.OnInteract += SaveInteract;
				RunButtonSelectable.OnInteract += RunInteract;
				_elevatorRoomType = ReflectionHelper.FindType("ElevatorRoom");
				_gameplayStateType = ReflectionHelper.FindType("GameplayState");
				if (_gameplayStateType != null)
					_gameplayroomPrefabOverrideField = _gameplayStateType.GetField("GameplayRoomPrefabOverride", BindingFlags.Public | BindingFlags.Static);

				// KMModSettings is not used here because this isn't strictly a configuration option.
				string path = Path.Combine(Application.persistentDataPath, "LastDynamicMission.txt");
				if (File.Exists(path)) InputField.text = File.ReadAllText(path);
			}
		}

		private void InputField_TabPressed(object sender, InputField.TabPressedEventArgs e)
		{
			if (listItems.Count == 0) return;
			e.SuppressKeyPress = true;
			suppressTextChanged = true;
			if (tabListIndex >= 0 && tabListIndex < listItems.Count)
				SetNormalColour(listItems[tabListIndex].GetComponent<Button>(), tabListIndex % 2 == 0 ? Color.white : new Color(0.875f, 0.875f, 0.875f));
			if (listItems.Count == 0 || string.IsNullOrEmpty(listItems[0].GetComponent<ModuleListItem>().ID))
			{
				suppressTextChanged = false;
				return;
			}
			if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
			{
				if (tabListIndex < 0) tabListIndex = listItems.Count;
				--tabListIndex;
			}
			else
			{
				++tabListIndex;
				if (tabListIndex >= listItems.Count) tabListIndex = -1;
			}
			if (tabListIndex < 0)
			{
				tabCursorPosition = ReplaceToken(tabStub, false);
				Scrollbar.value = 1;
			}
			else
			{
				SetNormalColour(listItems[tabListIndex].GetComponent<Button>(), new Color(1, 0.75f, 1));
				string id = listItems[tabListIndex].GetComponent<ModuleListItem>().ID;
				tabCursorPosition = ReplaceToken(id, false);
				float offset = (-((RectTransform) ModuleList.parent).rect.height + ((RectTransform) ModuleListItemPrefab.transform).sizeDelta.y * (tabListIndex * 2 + 1)) / 2;
				float limit = ModuleList.rect.height - ((RectTransform) ModuleList.parent).rect.height;
				Scrollbar.value = Math.Min(1, 1 - offset / limit);
			}
		}

		private void InputField_Scroll(object sender, EventArgs e)
		{
			if (ScrollView.gameObject.activeSelf) repositionScrollView = 15;
		}

		public void OnEnable()
		{
			InitModules();
			LoadProfiles();
			multipleBombsEnabled = GameObject.Find("MultipleBombs(Clone)") != null;
			factoryEnabled = GameObject.Find("FactoryService(Clone)") != null;
		}

		public void Update()
		{
			if (EventSystem.current.currentSelectedGameObject == InputField.gameObject)
			{
				var mousePosition = (Vector2) Input.mousePosition;
				if (mousePosition != oldMousePosition)
				{
					oldMousePosition = mousePosition;
					hoverDelay = 0.5f;
					Tooltip.gameObject.SetActive(false);
				}

				if (hoverDelay > 0)
				{
					hoverDelay -= Time.deltaTime;
					if (hoverDelay <= 0)
					{
						hoverDelay = 0;
						var rectTransform = (RectTransform) InputField.transform;
						if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, mousePosition, Camera.main, out var position2) &&
							Math.Abs(position2.x) < rectTransform.rect.width / 2 && Math.Abs(position2.y) < rectTransform.rect.height / 2)
						{
							// Get the hovered word.
							var charPosition = InputField.GetCharacterIndexFromPosition(position2);
							if (charPosition >= InputField.text.Length || InputField.text[charPosition] == ',' || InputField.text[charPosition] == '+') return;
							var matches = tokenRegex.Matches(InputField.text);
							foreach (Match match in matches)
							{
								if (charPosition < match.Index) break;
								if (charPosition <= match.Index + match.Length)
								{
									var group = match.Groups["ID"];
									if (group.Success)
									{
										if (charPosition < group.Index || charPosition >= group.Index + group.Length) break;
										var start = group.Value.LastIndexOfAny(new[] { ',', '+' }, charPosition - group.Index) + 1;
										var end = group.Value.IndexOfAny(new[] { ',', '+' }, charPosition - group.Index);
										if (end < 0) end = group.Length;
										var id = FixModuleID(group.Value.Substring(start, end - start));
										Tooltip.ID = id;
										ShowPopup((RectTransform) Tooltip.transform, position2, position2);
										Tooltip.gameObject.SetActive(true);

										var entry = moduleData.FirstOrDefault(e => e.ModuleType == id);
										if (entry != null)
										{
											Tooltip.Name = entry.DisplayName;
										}
										else
											Tooltip.Name = "";
									}
								}
							}
						}
					}
				}

				// If the cursor position has changed by means other than the Tab key, clear the completion popup.
				if (InputField.caretPosition != prevCursorPosition)
				{
					if (prevCursorPosition >= 0)
					{
						tabListIndex = -1;
						foreach (var item in listItems) Destroy(item);
						listItems.Clear();
						repositionScrollView = 0;
						ScrollView.gameObject.SetActive(false);
					}
					prevCursorPosition = InputField.caretPosition;
				}
			}
		}

		public void LateUpdate()
		{
			if (repositionScrollView > 0)
			{
				--repositionScrollView;
				if (repositionScrollView == 0)
				{
					ScrollView.gameObject.SetActive(true);
					var array = (UIVertex[]) cursorVertsField.GetValue(InputField);
					ShowPopup(ScrollView, array[0].position, array[3].position);
				}
			}
		}

		internal void LoadMission(Mission mission)
		{
			InputField.text = mission.Content;
			MissionText.text = "Mission: " + mission.Name;
			MissionText.color = Color.black;
			missionName = mission.Name;
		}

		private void ShowPopup(RectTransform popup, Vector3 cursorBottom, Vector3 cursorTop)
		{
			var inputFieldTransform = (RectTransform) InputField.transform;
			popup.pivot = new Vector2(0, 1);
			popup.localPosition = cursorBottom + new Vector3(0, 4);
			if (-popup.localPosition.y + popup.sizeDelta.y > inputFieldTransform.rect.height / 2)
			{
				popup.pivot = Vector2.zero;
				popup.localPosition = cursorTop - new Vector3(0, 4);
			}
			float d = popup.localPosition.x + popup.sizeDelta.x - inputFieldTransform.rect.width / 2;
			if (d > 0)
			{
				popup.localPosition -= new Vector3(d, 0);
			}
		}

		private static void SetNormalColour(Selectable selectable, Color color)
		{
			var colours = selectable.colors;
			colours.normalColor = color;
			selectable.colors = colours;
		}

		private bool SaveInteract()
		{
			void saveMission(string targetPath, string name)
			{
				File.WriteAllText(targetPath, InputField.text);
				LoadMission(new Mission(name, InputField.text, null));
			}

			// When the Mod Selector page is displayed, its KMSelectables are reassigned to the Mod Selector tablet itself.
			// We need to add the OK button to it, so SaveButtonSelectable.Parent is used to reference the tablet.
			Prompt.MakePrompt("Save Mission", missionName ?? "New Mission", CanvasTransform, SaveButtonSelectable.Parent, name =>
			{
				var targetPath = Path.Combine(DynamicMissionGenerator.MissionsFolder, name + ".txt");
				if (missionName != name && File.Exists(targetPath))
				{
					Confirmation.MakeConfirmation("Overwrite Mission?", "A mission with that name already exists, do you want to overwrite it?", CanvasTransform, SaveButtonSelectable.Parent, () => saveMission(targetPath, name));
					return;
				}

				saveMission(targetPath, name);
			});
			return false;
		}

		private bool RunInteract()
		{
			if (InputField == null)
				return false;
			if (string.IsNullOrEmpty(InputField.text))
			{
				Audio.PlayGameSoundAtTransform(KMSoundOverride.SoundEffect.Strike, transform);
				return false;
			}

			bool success = ParseTextToMission(InputField.text, out KMMission mission, out int? ruleseed, out var messages);
			if (!success)
			{
				Audio.PlayGameSoundAtTransform(KMSoundOverride.SoundEffect.Strike, transform);
				StartCoroutine(ShowErrorPopupCoroutine(string.Join("\n", messages.Take(5).ToArray()) + (messages.Count > 5 ? $"\n{messages.Count - 5} more" : "")));
				return false;
			}

			if (Application.isEditor)
			{
				StartCoroutine(ShowErrorPopupCoroutine("Success"));
				Debug.Log(JsonConvert.SerializeObject(mission, Formatting.Indented), this);
				return false;
			}
			Debug.Log(JsonConvert.SerializeObject(mission, Formatting.Indented), this);

			try
			{
				File.WriteAllText(Path.Combine(Application.persistentDataPath, "LastDynamicMission.txt"), InputField.text);
			}
			catch (Exception ex)
			{
				Debug.LogError("[Dynamic Mission Generator] Could not write LastDynamicMission.txt");
				Debug.LogException(ex, this);
			}

			if (ruleseed != null)
			{
				var obj = GameObject.Find("VanillaRuleModifierProperties");
				var dic = obj?.GetComponent<IDictionary<string, object>>();
				dic["RuleSeed"] = new object[] { ruleseed, true };
			}

			GameCommands.StartMission(mission, "-1");

			return false;
		}

		private IEnumerator ShowErrorPopupCoroutine(string message)
		{
			ErrorPopup.text = message;
			ErrorPopup.GetComponent<ContentSizeFitter>().SetLayoutVertical();
			ErrorPopup.transform.parent.gameObject.SetActive(true);
			yield return new WaitForSeconds(5);
			ErrorPopup.transform.parent.gameObject.SetActive(false);
		}

		public void TextChanged(string newText)
		{
			bool inserted = newText.Length > prevText.Length;
			prevCursorPosition = -1;
			prevText = newText;
			if (suppressTextChanged) return;
			ErrorPopup.transform.parent.gameObject.SetActive(false);
			StopAllCoroutines();
			tabListIndex = -1;
			tabCursorPosition = InputField.caretPosition;

			if (inserted)
			{
				if (InputField.caretPosition >= 2 && newText[InputField.caretPosition - 1] == ',' && newText[InputField.caretPosition - 2] == ' ')
				{
					// If a comma was typed immediately after an auto-inserted space, remove the space and recurse.
					StartCoroutine(SetSelectionCoroutine(InputField.caretPosition - 1));
					InputField.text = newText.Remove(InputField.caretPosition - 2, 1);
					return;
				}

				if (InputField.caretPosition >= 1 && newText[InputField.caretPosition - 1] == '\n')
				{
					// When a newline is typed, automatically indent the new line based on the previous line.
					int pos = newText.LastIndexOf('\n', InputField.caretPosition - 2) + 1;
					int length = 0;
					while (newText[pos + length] == ' ' || newText[pos + length] == '\t') ++length;
					if (length > 0)
					{
						suppressTextChanged = true;
						InputField.text = newText.Insert(InputField.caretPosition, newText.Substring(pos, length));
						StartCoroutine(SetSelectionCoroutine(InputField.caretPosition + length));
						suppressTextChanged = false;
					}
				}
			}

			foreach (var item in listItems) Destroy(item);
			listItems.Clear();
			var matches = tokenRegex.Matches(newText.Substring(0, InputField.caretPosition));
			if (matches.Count > 0)
			{
				var lastMatch = matches[matches.Count - 1];
				if (lastMatch.Index + lastMatch.Length == InputField.caretPosition)  // Show the popup on whitespace within a quoted module ID, but not whitespace after a token
				{
					if (lastMatch.Groups["T1"].Success)
					{
						string text = string.Format(lastMatch.Groups["T3"].Success ? "Time: {0}h {1}m {2}s" : "Time: {0}m {1}s",
							int.Parse(lastMatch.Groups["T1"].Value), int.Parse(lastMatch.Groups["T2"].Value), int.Parse(lastMatch.Groups["T3"].Value));
						var item = AddListItem(lastMatch.Value.TrimStart(), text, false);
						item.HighlightID(0, item.ID.Length);
					}
					else if (lastMatch.Groups["Strikes"].Success)
					{
						var item = AddListItem(lastMatch.Value.TrimStart(), "Strike limit: " + int.Parse(lastMatch.Groups["Strikes"].Value), false);
						item.HighlightID(0, item.ID.Length);
					}
					else if (lastMatch.Groups["Setting"].Success)
					{
						if (lastMatch.Groups["Setting"].Value.Equals("factory", StringComparison.InvariantCultureIgnoreCase))
						{
							if (factoryEnabled)
							{
								foreach (var m in factoryModeList)
								{
									if (m.ModuleType.StartsWith(lastMatch.Groups["Value"].Value, StringComparison.InvariantCultureIgnoreCase))
									{
										var item = AddListItem("factory:" + m.ModuleType, m.DisplayName, true);
										item.HighlightID(0, lastMatch.Groups["Value"].Length + 8);
									}
								}
							}
							else
							{
								var item = AddListItem(lastMatch.Groups["Setting"].Value + ":" + lastMatch.Groups["Value"].Value, "[Factory is not enabled]", false);
								item.HighlightID(0, item.ID.Length);
							}
						}
						else
						{
							var item = AddListItem("", lastMatch.Groups["Setting"].Value + ": " + lastMatch.Groups["Value"].Value, false);
							item.HighlightName(lastMatch.Groups["Setting"].Value.Length + 2, item.Name.Length - (lastMatch.Groups["Setting"].Value.Length + 2));
						}
					}
					else if (lastMatch.Groups["ID"].Success)
					{
						string s = FixModuleID(GetLastModuleID(lastMatch.Groups["ID"].Value));
						tabStub = s;
						if (!lastMatch.Groups["Count"].Success && !string.IsNullOrEmpty(lastMatch.Groups["ID"].Value) && lastMatch.Groups["ID"].Value.All(char.IsDigit))
						{
							var item = AddListItem($"{s}:00", "[Set time]", true);
							item.HighlightID(0, s.Length);
							item = AddListItem($"{s}X", "[Set strike limit]", true);
							item.HighlightID(0, s.Length);
							item = AddListItem($"{s}*", "[Set module pool count]", true);
							item.HighlightID(0, s.Length);
						}
						foreach (var m in moduleData)
						{
							bool id = m.ModuleType.StartsWith(s, StringComparison.InvariantCultureIgnoreCase);
							bool name = !id && m.DisplayName.StartsWith(s, StringComparison.InvariantCultureIgnoreCase);
							if (id || name)
							{
								var item = AddListItem(m.ModuleType, m.DisplayName, true);
								if (id) item.HighlightID(0, s.Length);
								else if (name) item.HighlightName(0, s.Length);
							}
						}
					}
				}
			}

			if (listItems.Count > 0)
			{
				repositionScrollView = 2;
			}
			else
			{
				repositionScrollView = 0;
				ScrollView.gameObject.SetActive(false);
			}
		}

		private static string GetLastModuleID(string list) => list.Substring(GetLastModuleIDPos(list));
		private static int GetLastModuleIDPos(string list) => list.LastIndexOfAny(new[] { ',', '+' }) + 1;
		private static string FixModuleID(string id) => id.Replace("\"", "").Replace("'", "").Trim();

		private ModuleListItem AddListItem(string id, string text, bool addClickEvent)
		{
			var item = Instantiate(ModuleListItemPrefab, ModuleList);
			if (listItems.Count % 2 != 0) SetNormalColour(item.GetComponent<Button>(), new Color(0.875f, 0.875f, 0.875f));
			if (addClickEvent) item.Click += ModuleListItem_Click;
			item.Name = text;
			item.ID = id;
			listItems.Add(item.gameObject);
			return item;
		}

		private void ModuleListItem_Click(object sender, EventArgs e)
		{
			string id = ((ModuleListItem) sender).ID;
			suppressTextChanged = true;
			ReplaceToken(id, !id.EndsWith("*"));
			suppressTextChanged = false;
		}

		private int ReplaceToken(string id, bool space)
		{
			var match = tokenRegex.Matches(InputField.text.Substring(0, tabCursorPosition)).Cast<Match>().Last();

			int startIndex;
			if (match.Groups["ID"].Success)
			{
				startIndex = match.Groups["ID"].Index + GetLastModuleIDPos(match.Groups["ID"].Value);
				if (id.Contains(' ') && match.Groups["ID"].Value.Take(startIndex - match.Groups["ID"].Index).Count(c => c == '"' || c == '\'') % 2 == 0)
					id = "\"" + id + "\"";
				if (space) id += " ";
			}
			else startIndex = match.Groups[1].Index;
			InputField.text = InputField.text.Remove(startIndex, tabCursorPosition - startIndex).Insert(startIndex, id);
			InputField.Select();
			StartCoroutine(SetSelectionCoroutine(startIndex + id.Length));
			return startIndex + id.Length;
		}

		private IEnumerator SetSelectionCoroutine(int pos)
		{
			yield return null;
			InputField.caretPosition = pos;
			InputField.ForceLabelUpdate();
			if (suppressTextChanged) suppressTextChanged = false;
			else TextChanged(InputField.text);
			prevCursorPosition = -1;
		}

		private void InitModules()
		{
			moduleData.Clear();
			moduleData.Add(new ModuleData("ALL_SOLVABLE", "[All solvable modules]"));
			moduleData.Add(new ModuleData("ALL_NEEDY", "[All needy modules]"));
			moduleData.Add(new ModuleData("ALL_VANILLA", "[All vanilla solvable modules]"));
			moduleData.Add(new ModuleData("ALL_MODS", "[All mod solvable modules]"));
			moduleData.Add(new ModuleData("ALL_VANILLA_NEEDY", "[All vanilla needy modules]"));
			moduleData.Add(new ModuleData("ALL_MODS_NEEDY", "[All mod needy modules]"));
			moduleData.Add(new ModuleData("frontonly", "[Front face only]"));
			moduleData.Add(new ModuleData("nopacing", "[Disable pacing events]"));
			moduleData.Add(new ModuleData("widgets:", "[Set widget count]"));
			moduleData.Add(new ModuleData("ruleseed:", "[Set rule seed]"));
			moduleData.Add(new ModuleData("needyactivationtime:", "[Set needy activation time in seconds]"));
			if (factoryEnabled) moduleData.Add(new ModuleData("factory:", "[Set Factory mode]"));
			moduleData.Add(new ModuleData("Wires", "Wires"));
			moduleData.Add(new ModuleData("Keypad", "Keypad"));
			moduleData.Add(new ModuleData("Memory", "Memory"));
			moduleData.Add(new ModuleData("Maze", "Maze"));
			moduleData.Add(new ModuleData("Password", "Password"));
			moduleData.Add(new ModuleData("BigButton", "The Button"));
			moduleData.Add(new ModuleData("Simon", "Simon Says"));
			moduleData.Add(new ModuleData("WhosOnFirst", "Who's On First"));
			moduleData.Add(new ModuleData("Morse", "Morse Code"));
			moduleData.Add(new ModuleData("Venn", "Complicated Wires"));
			moduleData.Add(new ModuleData("WireSequence", "Wire Sequence"));
			moduleData.Add(new ModuleData("NeedyVentGas", "Venting Gas"));
			moduleData.Add(new ModuleData("NeedyCapacitor", "Capacitor Discharge"));
			moduleData.Add(new ModuleData("NeedyKnob", "Knob"));

			if (Application.isEditor)
			{
				moduleData.Add(new ModuleData($"Space Test", $"Space Test"));
				for (int i = 0; i < 30; ++i)
				{
					moduleData.Add(new ModuleData($"ScrollTest{i:00}", $"Scroll Test {i}"));
				}
			}

			if (DynamicMissionGenerator.ModSelectorApi != null)
			{
				var assembly = DynamicMissionGenerator.ModSelectorApi.GetType().Assembly;
				var serviceType = assembly.GetType("ModSelectorService");
				object service = serviceType.GetProperty("Instance").GetValue(null, null);
				var allSolvableModules = (IDictionary) serviceType.GetField("_allSolvableModules", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);
				var allNeedyModules = (IDictionary) serviceType.GetField("_allNeedyModules", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);

				foreach (object entry in allSolvableModules.Cast<object>().Concat(allNeedyModules.Cast<object>()))
				{
					string id = (string) entry.GetType().GetProperty("Key").GetValue(entry, null);
					object value = entry.GetType().GetProperty("Value").GetValue(entry, null);
					string name = (string) value.GetType().GetProperty("ModuleName").GetValue(value, null);
					moduleData.Add(new ModuleData(id, name));
				}
			}

			moduleData.Sort((a, b) => a.ModuleType.CompareTo(b.ModuleType));
		}

		private void LoadProfiles()
		{
			profiles.Clear();
			string path = Path.Combine(Application.persistentDataPath, "ModProfiles");
			if (!Directory.Exists(path)) return;

			var allSolvableModules = new HashSet<string>((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["AllSolvableModules"]);
			var allNeedyModules = new HashSet<string>((IEnumerable<string>) DynamicMissionGenerator.ModSelectorApi["AllNeedyModules"]);

			try
			{
				foreach (string file in Directory.GetFiles(path, "*.json"))
				{
					try
					{
						using var reader = new StreamReader(file);
						var profile = new JsonSerializer().Deserialize<Profile>(new JsonTextReader(reader));
						if (profile.DisabledList == null)
						{
							Debug.LogWarning($"[Profile Revealer] Could not load profile {Path.GetFileName(file)}");
							continue;
						}

						string profileName = Path.GetFileNameWithoutExtension(file);
						profiles.Add(profileName, profile);
						// Don't list defuser profiles that disable no modules as completion options.
						bool any = false;
						if (profile.Operation == ProfileType.Expert || profile.DisabledList.Where(m => allSolvableModules.Contains(m)).Any())
						{
							any = true;
							moduleData.Add(new ModuleData("profile:" + profileName, profileName + " (solvable modules enabled by profile)"));
						}
						if (profile.Operation == ProfileType.Expert || profile.DisabledList.Where(m => allNeedyModules.Contains(m)).Any())
						{
							any = true;
							moduleData.Add(new ModuleData("needyprofile:" + profileName, profileName + " (needy modules enabled by profile)"));
						}
						if (!any)
						{
							Debug.Log($"[Dynamic Mission Generator] Not listing {profileName} as it is a defuser profile that seems to disable no modules.");
						}
					}
					catch (Exception ex)
					{
						Debug.LogWarning($"[Dynamic Mission Generator] Could not load profile {Path.GetFileName(file)}");
						Debug.LogException(ex, this);
					}
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[Dynamic Mission Generator] Could not load profiles");
				Debug.LogException(ex, this);
			}
		}

		private bool ParseTextToMission(string text, out KMMission mission, out int? ruleseed, out List<string> messages)
		{
			messages = new List<string>();

			var moduleProfiles = new List<ReadOnlyCollection<string>>();
			var currentBombModuleProfiles = new List<string>();
			var matches = tokenRegex.Matches(text);

			KMGeneratorSetting currentBomb = null;
			List<KMGeneratorSetting> bombs = null;

			int bombRepeatCount = 0;
			int? defaultTime = null, defaultStrikes = null, defaultNeedyActivationTime = null, defaultWidgetCount = null, defaultRuleSeed = null;
			bool defaultFrontOnly = false;
			bool timeSpecified = false, strikesSpecified = false, needyActivationTimeSpecified = false, widgetCountSpecified = false, ruleSeedSpecified = false;
			bool anySolvableModules = false;
			int? factoryMode = null;

			mission = ScriptableObject.CreateInstance<KMMission>();
			mission.PacingEventsEnabled = true;
			List<KMComponentPool> pools = null;

			void newBomb()
			{
				currentBomb = new KMGeneratorSetting() { FrontFaceOnly = defaultFrontOnly };
				timeSpecified = strikesSpecified = needyActivationTimeSpecified = widgetCountSpecified = ruleSeedSpecified = anySolvableModules = false;
				pools = new List<KMComponentPool>();
				currentBombModuleProfiles = new List<string>();
			}

			void validateBomb(List<string> messages)
			{
				if (!anySolvableModules) messages.Add("No solvable modules" + (bombs != null ? $" on bomb {bombs.Count + 1}" : ""));
				currentBomb.ComponentPools = pools;
				if (!timeSpecified) currentBomb.TimeLimit = defaultTime ?? currentBomb.GetComponentCount() * 120;
				if (!strikesSpecified) currentBomb.NumStrikes = defaultStrikes ?? Math.Max(3, currentBomb.GetComponentCount() / 12);
				if (!needyActivationTimeSpecified && defaultNeedyActivationTime.HasValue) currentBomb.TimeBeforeNeedyActivation = defaultNeedyActivationTime.Value;
				if (!widgetCountSpecified && defaultWidgetCount.HasValue) currentBomb.OptionalWidgetCount = defaultWidgetCount.Value;
				if (currentBomb.GetComponentCount() > GetMaxModules())
					messages.Add($"Too many modules for any bomb casing ({currentBomb.GetComponentCount()} > {GetMaxModules()})" + (bombs != null ? $" on bomb {bombs.Count + 1}" : ""));
				moduleProfiles.Add(currentBombModuleProfiles.AsReadOnly());
			}

			foreach (Match match in matches)
			{
				if (match.Groups["Time1"].Success)
				{
					if (timeSpecified) messages.Add("Time specified multiple times");
					timeSpecified = true;

					var time = match.Groups["Time3"].Success ?
						int.Parse(match.Groups["Time1"].Value) * 3600 + int.Parse(match.Groups["Time2"].Value) * 60 + int.Parse(match.Groups["Time3"].Value) :
						int.Parse(match.Groups["Time1"].Value) * 60 + int.Parse(match.Groups["Time2"].Value);
					if (time <= 0) messages.Add("Invalid time limit");

					if (currentBomb != null) currentBomb.TimeLimit = time;
					else defaultTime = time;
				}
				else if (match.Groups["Strikes"].Success || match.Groups["Setting"].Value.Equals("strikes", StringComparison.InvariantCultureIgnoreCase))
				{
					if (strikesSpecified) messages.Add("Strikes specified multiple times");
					strikesSpecified = true;

					var strikes = int.Parse(match.Groups["Strikes"].Success ? match.Groups["Strikes"].Value : match.Groups["Value"].Value);
					if (strikes <= 0) messages.Add("Invalid strike limit");

					if (currentBomb != null) currentBomb.NumStrikes = strikes;
					else defaultStrikes = strikes;
				}
				else if (match.Groups["Setting"].Success)
				{
					switch (match.Groups["Setting"].Value.ToLowerInvariant())
					{
						case "needyactivationtime":
							if (needyActivationTimeSpecified) messages.Add("Needy activation time specified multiple times");
							needyActivationTimeSpecified = true;

							var needyActivationTime = int.Parse(match.Groups["Value"].Value);
							if (needyActivationTime < 0) messages.Add("Invalid needy activation time");

							if (currentBomb != null) currentBomb.TimeBeforeNeedyActivation = needyActivationTime;
							else defaultNeedyActivationTime = needyActivationTime;
							break;
						case "widgets":
							if (widgetCountSpecified) messages.Add("Widget count specified multiple times");
							widgetCountSpecified = true;

							var widgetCount = int.Parse(match.Groups["Value"].Value);
							if (widgetCount < 0) messages.Add("Invalid widget count");

							if (currentBomb != null) currentBomb.OptionalWidgetCount = widgetCount;
							else defaultWidgetCount = widgetCount;
							break;
						case "frontonly":
							if (currentBomb != null) currentBomb.FrontFaceOnly = true;
							else defaultFrontOnly = true;
							break;
						case "nopacing":
							if (bombs != null && currentBomb != null) messages.Add("nopacing cannot be a bomb-level setting");
							else mission.PacingEventsEnabled = false;
							break;
						case "factory":
							if (bombs != null && currentBomb != null) messages.Add("Factory mode cannot be a bomb-level setting");
							else if (factoryMode.HasValue) messages.Add("Factory mode specified multiple times");
							else if (!factoryEnabled && !Application.isEditor) messages.Add("Factory does not seem to be enabled");
							else
							{
								for (factoryMode = 0; factoryMode < factoryModeList.Length; ++factoryMode)
								{
									if (factoryModeList[factoryMode.Value].ModuleType.Equals(match.Groups["Value"].Value, StringComparison.InvariantCultureIgnoreCase)) break;
								}
								if (factoryMode >= factoryModeList.Length)
								{
									messages.Add("Invalid factory mode");
								}
							}
							break;
						case "ruleseed":
							if (bombs != null && currentBomb != null) messages.Add("Rule seed cannot be a bomb-level setting");
							else if (ruleSeedSpecified) messages.Add("Rule seed specified multiple times");
							ruleSeedSpecified = true;

							if (match.Groups["Value"].Value == "random")
								defaultRuleSeed = null;
							else
							{
								if (int.TryParse(match.Groups["Value"].Value, out var ruleSeed) && ruleSeed >= 0) defaultRuleSeed = ruleSeed;
								else messages.Add("Invalid rule seed");
							}
							break;
					}
				}
				else if (match.Groups["ID"].Success)
				{
					if (match.Groups["Error"].Success) messages.Add("Unclosed quote");
					if (bombs == null)
					{
						if (currentBomb == null)
						{
							newBomb();
							if (defaultTime.HasValue) { timeSpecified = true; currentBomb.TimeLimit = defaultTime.Value; }
							if (defaultStrikes.HasValue) { strikesSpecified = true; currentBomb.NumStrikes = defaultStrikes.Value; }
							if (defaultNeedyActivationTime.HasValue) { needyActivationTimeSpecified = true; currentBomb.TimeBeforeNeedyActivation = defaultNeedyActivationTime.Value; }
							if (defaultWidgetCount.HasValue) { widgetCountSpecified = true; currentBomb.OptionalWidgetCount = defaultWidgetCount.Value; }
							if (defaultRuleSeed.HasValue) { ruleSeedSpecified = true; }
						}
					}
					else
					{
						if (currentBomb == null)
						{
							messages.Add("Unexpected module pool");
							anySolvableModules = true;
							continue;
						}
					}

					string moduleList = FixModuleID(match.Groups["ID"].Value);
					int moduleCount = match.Groups["Count"].Success ? int.Parse(match.Groups["Count"].Value) : 1;
					bool noDuplicates = match.Groups["NoDuplicate"].Success;

					if (moduleCount <= 0)
						messages.Add("Invalid module pool count");

					bool poolHasSolvableModules;

					if (noDuplicates)
					{
						IList<KMComponentPool> individualPools = CreateIndividualPools(moduleList, moduleCount, messages, currentBombModuleProfiles, out poolHasSolvableModules);
						pools.AddRange(individualPools);
					}
					else
					{
						KMComponentPool pool = CreateRegularPool(moduleList, moduleCount, messages, currentBombModuleProfiles, out poolHasSolvableModules);
						pools.Add(pool);
					}

					anySolvableModules = anySolvableModules || poolHasSolvableModules;
				}
				else if (match.Groups["Open"].Success)
				{
					if (currentBomb != null) messages.Add("Unexpected '('");
					if (!multipleBombsEnabled && !Application.isEditor) messages.Add("Multiple Bombs does not seem to be enabled");
					bombRepeatCount = match.Groups["Count"].Success ? int.Parse(match.Groups["Count"].Value) : 1;
					if (bombRepeatCount <= 0) messages.Add("Invalid bomb repeat count");
					if (bombs == null) bombs = new List<KMGeneratorSetting>();
					newBomb();
				}
				else if (match.Groups["Close"].Success)
				{
					if (currentBomb == null) messages.Add("Unexpected ')'");
					else
					{
						validateBomb(messages);
						for (; bombRepeatCount > 0; --bombRepeatCount) bombs.Add(currentBomb);
						currentBomb = null;
					}
				}
			}

			if (bombs == null)
			{
				if (currentBomb == null) messages.Add("No solvable modules");
				else
				{
					validateBomb(messages);
					mission.GeneratorSetting = currentBomb;
				}
			}
			else if (bombs.Count == 0) messages.Add("No solvable modules");

			ruleseed = null;
			if (ruleSeedSpecified)
			{
				var obj = GameObject.Find("VanillaRuleModifierProperties");
				var dic = obj?.GetComponent<IDictionary<string, object>>();
				if (obj == null || dic == null)
					messages.Add("Rule seed modifier mod is not installed or disabled.");
				else
					ruleseed = defaultRuleSeed ?? UnityEngine.Random.Range(1, 1000);
			}

			if (messages.Count > 0)
			{
				Destroy(mission);
				mission = null;
				return false;
			}
			if (bombs != null)
			{
				mission.GeneratorSetting = bombs[0];
				if (bombs.Count > 1)
				{
					mission.GeneratorSetting.ComponentPools.Add(new KMComponentPool() { Count = bombs.Count - 1, ModTypes = new List<string>() { "Multiple Bombs" } });
					for (int i = 1; i < bombs.Count; ++i)
					{
						if (bombs[i] != mission.GeneratorSetting)
							mission.GeneratorSetting.ComponentPools.Add(new KMComponentPool() { ModTypes = new List<string>() { $"Multiple Bombs:{i}:{JsonConvert.SerializeObject(bombs[i])}" } });
					}
				}
			}
			if (factoryMode.HasValue)
				mission.GeneratorSetting.ComponentPools.Add(new KMComponentPool() { Count = factoryMode.Value, ModTypes = new List<string>() { "Factory Mode" } });
			messages = null;
			mission.DisplayName = "Custom Freeplay";
			DynamicMissionGeneratorApi.Instance.ModuleProfiles = moduleProfiles.AsReadOnly();
			return true;
		}

		private KMComponentPool CreateRegularPool(string moduleList, int moduleCount, List<string> messages, List<string> currentBombModuleProfiles, out bool hasAnySolvableModules)
		{
			var allSolvableModules = new HashSet<string>((IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["AllSolvableModules"] ?? new string[0]);
			var allNeedyModules = new HashSet<string>((IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["AllNeedyModules"] ?? new string[0]);
			var enabledSolvableModules = new HashSet<string>(allSolvableModules.Except((IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["DisabledSolvableModules"] ?? new string[0]));
			var enabledNeedyModules = new HashSet<string>(allNeedyModules.Except((IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["DisabledNeedyModules"] ?? new string[0]));

			hasAnySolvableModules = false;
			string profileName = null;

			KMComponentPool pool = new KMComponentPool
			{
				Count = moduleCount,
				ComponentTypes = new List<KMComponentPool.ComponentTypeEnum>(),
				ModTypes = new List<string>()
			};

			switch (moduleList)
			{
				case "ALL_SOLVABLE":
					hasAnySolvableModules = true;
					pool.AllowedSources = KMComponentPool.ComponentSource.Base | KMComponentPool.ComponentSource.Mods;
					pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_SOLVABLE;
					break;
				case "ALL_NEEDY":
					pool.AllowedSources = KMComponentPool.ComponentSource.Base | KMComponentPool.ComponentSource.Mods;
					pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_NEEDY;
					break;
				case "ALL_VANILLA":
					hasAnySolvableModules = true;
					pool.AllowedSources = KMComponentPool.ComponentSource.Base;
					pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_SOLVABLE;
					break;
				case "ALL_MODS":
					hasAnySolvableModules = true;
					pool.AllowedSources = KMComponentPool.ComponentSource.Mods;
					pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_SOLVABLE;
					break;
				case "ALL_VANILLA_NEEDY":
					pool.AllowedSources = KMComponentPool.ComponentSource.Base;
					pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_NEEDY;
					break;
				case "ALL_MODS_NEEDY":
					pool.AllowedSources = KMComponentPool.ComponentSource.Mods;
					pool.SpecialComponentType = KMComponentPool.SpecialComponentTypeEnum.ALL_NEEDY;
					break;
				default:
					bool useProfile = moduleList.StartsWith("profile:", StringComparison.InvariantCultureIgnoreCase);
					bool useNeedyProfile = !useProfile && moduleList.StartsWith("needyprofile:", StringComparison.InvariantCultureIgnoreCase);

					if (useProfile || useNeedyProfile)
					{
						profileName = moduleList.Substring(useNeedyProfile ? 13 : 8);
						if (!profiles.TryGetValue(profileName, out var profile))
						{
							messages.Add($"No profile named '{profileName}' was found.");
						}
						else
						{
							currentBombModuleProfiles.Add(profileName);
							Debug.Log("[Dynamic Mission Generator] Disabled list: " + string.Join(", ", profile.DisabledList.ToArray()));
							pool.ModTypes.AddRange((useNeedyProfile ? enabledNeedyModules : enabledSolvableModules).Except(profile.DisabledList));
							if (pool.ModTypes.Count == 0)
							{
								messages.Add($"Profile '{profileName}' enables no valid modules.");
							}
							else
							{
								hasAnySolvableModules = hasAnySolvableModules || useProfile;
							}
						}
					}
					else
					{
						foreach (string id in moduleList.Split(',', '+').Select(s => s.Trim()))
						{
							if(VanillaModulesHelper.VanillaSolvableModules.Contains(id))
							{
								hasAnySolvableModules = true;
								pool.ComponentTypes.Add(VanillaModulesHelper.VanillaModuleNameToEnumMap[id]);
							}
							else if (VanillaModulesHelper.VanillaNeedyModules.Contains(id))
							{
								pool.ComponentTypes.Add(VanillaModulesHelper.VanillaModuleNameToEnumMap[id]);
							}
							else if (!allSolvableModules.Contains(id) && !allNeedyModules.Contains(id))
								messages.Add($"'{id}' is an unknown module ID.");
							else if (!enabledSolvableModules.Contains(id) && !enabledNeedyModules.Contains(id))
								messages.Add($"'{id}' is disabled.");
							else
							{
								hasAnySolvableModules = hasAnySolvableModules || allSolvableModules.Contains(id);
								pool.ModTypes.Add(id);
							}
						}
					}
					break;
			}
			
			if (pool.ModTypes.Count == 0)
				pool.ModTypes = null;
			
			if (pool.ComponentTypes.Count == 0)
				pool.ComponentTypes = null;
			
			return pool;
		}

		private IList<KMComponentPool> CreateIndividualPools(string moduleList, int poolCount, List<string> messages, List<string> currentBombModuleProfiles, out bool hasAnySolvableModules)
		{
			var modDisabledSolvableModules = (IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["DisabledSolvableModules"] ?? new string[0];
			var modDisabledNeedyModules = (IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["DisabledNeedyModules"] ?? new string[0];

			var modEnabledSolvableModules = ((IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["AllSolvableModules"] ?? new string[0]).Except(modDisabledSolvableModules);
			var modEnableNeedyModules = ((IEnumerable<string>)DynamicMissionGenerator.ModSelectorApi?["AllNeedyModules"] ?? new string[0]).Except(modDisabledNeedyModules);
			
			var allEnabledSolvableModules = modEnabledSolvableModules.Concat(VanillaModulesHelper.VanillaSolvableModules);
			var allEnabledNeedyModules = modEnableNeedyModules.Concat(VanillaModulesHelper.VanillaNeedyModules);

			hasAnySolvableModules = false;
			string profileName = null;

			IEnumerable<string> moduleIds;

			switch (moduleList)
			{
				case "ALL_SOLVABLE":
					hasAnySolvableModules = true;
					moduleIds = allEnabledSolvableModules;
					break;
				case "ALL_NEEDY":
					moduleIds = allEnabledNeedyModules;
					break;
				case "ALL_VANILLA":
					hasAnySolvableModules = true;
					moduleIds = VanillaModulesHelper.VanillaSolvableModules;
					break;
				case "ALL_MODS":
					hasAnySolvableModules = true;
					moduleIds = modEnabledSolvableModules;
					break;
				case "ALL_VANILLA_NEEDY":
					moduleIds = VanillaModulesHelper.VanillaNeedyModules;
					break;
				case "ALL_MODS_NEEDY":
					moduleIds = modEnableNeedyModules;
					break;
				default:
					bool useProfile = moduleList.StartsWith("profile:", StringComparison.InvariantCultureIgnoreCase);
					bool useNeedyProfile = !useProfile && moduleList.StartsWith("needyprofile:", StringComparison.InvariantCultureIgnoreCase);

					if (useProfile || useNeedyProfile)
					{
						profileName = moduleList.Substring(useNeedyProfile ? 13 : 8);
						if (!profiles.TryGetValue(profileName, out var profile))
						{
							messages.Add($"No profile named '{profileName}' was found.");
							moduleIds = new List<string>(0);
						}
						else
						{
							currentBombModuleProfiles.Add(profileName);
							Debug.Log("[Dynamic Mission Generator] Disabled list: " + string.Join(", ", profile.DisabledList.ToArray()));
							moduleIds = (useNeedyProfile
								? modEnableNeedyModules
								: modEnabledSolvableModules).Except(profile.DisabledList);

							if (moduleIds.Count() == 0)
							{
								messages.Add($"Profile '{profileName}' enables no valid modules.");
							}
							else
							{
								hasAnySolvableModules = hasAnySolvableModules || useProfile;
							}
						}
					}
					else
					{
						List<string> curatedModdedIds = new List<string>();

						foreach (string id in moduleList.Split(',', '+').Select(s => s.Trim()))
						{
							if (VanillaModulesHelper.VanillaSolvableModules.Contains(id))
							{
								hasAnySolvableModules = true;
								curatedModdedIds.Add(id);
							}
							else if (VanillaModulesHelper.VanillaNeedyModules.Contains(id))
							{
								curatedModdedIds.Add(id);
							}
							else if (modDisabledSolvableModules.Contains(id) || allEnabledNeedyModules.Contains(id))
							{
								messages.Add($"'{id}' is disabled.");
							}
							else if (!modEnabledSolvableModules.Contains(id) && !modDisabledNeedyModules.Contains(id))
							{
								messages.Add($"'{id}' is an unknown module ID.");
							}
							else
							{
								curatedModdedIds.Add(id);
								hasAnySolvableModules = hasAnySolvableModules || modEnabledSolvableModules.Contains(id);
							}
						}

						moduleIds = curatedModdedIds;
					}
					break;
			}

			int moduleFoundCount = moduleIds.Count();
			if (moduleFoundCount == 0)
			{
				messages.Add($"No valid modules found");
			}
			else if (moduleFoundCount < poolCount)
			{
				messages.Add($"Requested {poolCount} unique modules but only found {moduleFoundCount} valid modules");
			}

			IEnumerable<string> selectedUniqueModuleIds = moduleIds.OrderBy(_ => UnityEngine.Random.value).Take(poolCount);
			IList<KMComponentPool> pools = new List<KMComponentPool>(poolCount);

			foreach (string moduleId in selectedUniqueModuleIds)
			{
				KMComponentPool pool = new KMComponentPool { Count = 1 };

				if(VanillaModulesHelper.VanillaModuleNameToEnumMap.ContainsKey(moduleId))
				{
					pool.ComponentTypes = new List<KMComponentPool.ComponentTypeEnum>() { VanillaModulesHelper.VanillaModuleNameToEnumMap[moduleId] };
				}
				else
				{
					pool.ModTypes = new List<string> { moduleId };
				}

				pools.Add(pool);
			}

			return pools;
		}

		private int GetMaxModules()
		{
			if (Application.isEditor) return 11;
			GameObject roomPrefab = (GameObject) _gameplayroomPrefabOverrideField.GetValue(null);
			if (roomPrefab == null) return GameInfo.GetMaximumBombModules();
			return roomPrefab.GetComponentInChildren(_elevatorRoomType, true) != null ? 54 : GameInfo.GetMaximumBombModules();
		}

		private static Type _gameplayStateType;
		private static FieldInfo _gameplayroomPrefabOverrideField;

		private static Type _elevatorRoomType;

		private struct Profile
		{
			public HashSet<string> DisabledList;
			public ProfileType Operation;
		}

		private enum ProfileType
		{
			Expert,
			Defuser
		}
	}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Paradise.Core.Avatars;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;

namespace Paradise.Editor;

public static class SceneAdapters
{
	[CompilerGenerated]
	private sealed class _003C_003Ec__DisplayClass10_0
	{
		public MapStreamer s;

		internal bool _003CEnvironmentChildren_003Eb__0()
		{
			return s.ShowClouds;
		}

		internal void _003CEnvironmentChildren_003Eb__1(bool v)
		{
			s.ShowClouds = v;
		}

		internal bool _003CEnvironmentChildren_003Eb__2()
		{
			return s.ShowStars;
		}

		internal void _003CEnvironmentChildren_003Eb__3(bool v)
		{
			s.ShowStars = v;
		}

		internal bool _003CEnvironmentChildren_003Eb__4()
		{
			return s.ShowPowerWires;
		}

		internal void _003CEnvironmentChildren_003Eb__5(bool v)
		{
			s.ShowPowerWires = v;
		}
	}

	[CompilerGenerated]
	private sealed class _003C_003Ec__DisplayClass9_0
	{
		public MapStreamer s;

		public Func<MapStreamer.EditorCell, SceneNode> _003C_003E9__20;

		public Predicate<MapStreamer.EditorCell> _003C_003E9__21;

		public Func<MapStreamer.EditorCell, SceneNode> _003C_003E9__22;

		public Predicate<MapStreamer.EditorCell> _003C_003E9__23;

		public Func<MapStreamer.SceneHandle, int, SceneNode> _003C_003E9__24;

		public Func<MapStreamer.MapTile, SceneNode> _003C_003E9__25;

		internal int _003CWorldChildren_003Eb__0()
		{
			return s.EditorIslandPieces().Count;
		}

		internal IEnumerable<SceneNode> _003CWorldChildren_003Eb__1()
		{
			return from p in s.EditorIslandPieces()
				select Node(p.Node, "Island " + p.Key, () => true);
		}

		internal bool _003CWorldChildren_003Eb__2()
		{
			return s.ShowIslandLod;
		}

		internal void _003CWorldChildren_003Eb__3(bool v)
		{
			s.ShowIslandLod = v;
		}

		internal int _003CWorldChildren_003Eb__4()
		{
			return s.EditorAreas().Count;
		}

		internal IEnumerable<SceneNode> _003CWorldChildren_003Eb__5()
		{
			return from c in s.EditorAreas()
				select Cell(s, c);
		}

		internal SceneNode _003CWorldChildren_003Eb__20(MapStreamer.EditorCell c)
		{
			return Cell(s, c);
		}

		internal bool _003CWorldChildren_003Eb__6()
		{
			return s.EditorAreas().Exists((MapStreamer.EditorCell c) => !s.EditorGetHidden(c));
		}

		internal bool _003CWorldChildren_003Eb__21(MapStreamer.EditorCell c)
		{
			return !s.EditorGetHidden(c);
		}

		internal void _003CWorldChildren_003Eb__7(bool v)
		{
			foreach (MapStreamer.EditorCell item in s.EditorAreas())
			{
				s.EditorSetHidden(item, !v);
			}
		}

		internal int _003CWorldChildren_003Eb__8()
		{
			return s.EditorSectors().Count;
		}

		internal IEnumerable<SceneNode> _003CWorldChildren_003Eb__9()
		{
			return from c in s.EditorSectors()
				select Cell(s, c);
		}

		internal SceneNode _003CWorldChildren_003Eb__22(MapStreamer.EditorCell c)
		{
			return Cell(s, c);
		}

		internal bool _003CWorldChildren_003Eb__10()
		{
			return s.EditorSectors().Exists((MapStreamer.EditorCell c) => !s.EditorGetHidden(c));
		}

		internal bool _003CWorldChildren_003Eb__23(MapStreamer.EditorCell c)
		{
			return !s.EditorGetHidden(c);
		}

		internal void _003CWorldChildren_003Eb__11(bool v)
		{
			foreach (MapStreamer.EditorCell item in s.EditorSectors())
			{
				s.EditorSetHidden(item, !v);
			}
		}

		internal int _003CWorldChildren_003Eb__12()
		{
			return s.Scenes.Count;
		}

		internal IEnumerable<SceneNode> _003CWorldChildren_003Eb__13()
		{
			return s.Scenes.ToArray().Select((MapStreamer.SceneHandle h, int i) => Scene(s, h, i));
		}

		internal SceneNode _003CWorldChildren_003Eb__24(MapStreamer.SceneHandle h, int i)
		{
			return Scene(s, h, i);
		}

		internal int _003CWorldChildren_003Eb__14()
		{
			return s.MapTiles.Count;
		}

		internal IEnumerable<SceneNode> _003CWorldChildren_003Eb__15()
		{
			return from t in s.MapTiles.ToArray()
				select MapTile(s, t);
		}

		internal SceneNode _003CWorldChildren_003Eb__25(MapStreamer.MapTile t)
		{
			return MapTile(s, t);
		}

		internal IEnumerable<SceneNode> _003CWorldChildren_003Eb__17()
		{
			return EnvironmentChildren(s);
		}
	}

	[CompilerGenerated]
	private sealed class _003CCellChildren_003Ed__13 : IEnumerable<SceneNode>, IEnumerable, IEnumerator<SceneNode>, IEnumerator, IDisposable
	{
		private int _003C_003E1__state;

		private SceneNode _003C_003E2__current;

		private int _003C_003El__initialThreadId;

		private IReadOnlyList<MapStreamer.Node> nodes;

		public IReadOnlyList<MapStreamer.Node> _003C_003E3__nodes;

		private Func<bool> alive;

		public Func<bool> _003C_003E3__alive;

		private PlacementRenderer plc;

		public PlacementRenderer _003C_003E3__plc;

		private string id;

		public string _003C_003E3__id;

		private int _003Ci_003E5__2;

		SceneNode IEnumerator<SceneNode>.Current
		{
			[DebuggerHidden]
			get
			{
				return _003C_003E2__current;
			}
		}

		object IEnumerator.Current
		{
			[DebuggerHidden]
			get
			{
				return _003C_003E2__current;
			}
		}

		[DebuggerHidden]
		public _003CCellChildren_003Ed__13(int _003C_003E1__state)
		{
			this._003C_003E1__state = _003C_003E1__state;
			_003C_003El__initialThreadId = Environment.CurrentManagedThreadId;
		}

		[DebuggerHidden]
		void IDisposable.Dispose()
		{
			_003C_003E1__state = -2;
		}

		private bool MoveNext()
		{
			switch (_003C_003E1__state)
			{
			default:
				return false;
			case 0:
				_003C_003E1__state = -1;
				_003Ci_003E5__2 = 0;
				goto IL_0082;
			case 1:
				_003C_003E1__state = -1;
				_003Ci_003E5__2++;
				goto IL_0082;
			case 2:
				{
					_003C_003E1__state = -1;
					break;
				}
				IL_0082:
				if (_003Ci_003E5__2 < nodes.Count)
				{
					_003C_003E2__current = Node(nodes[_003Ci_003E5__2], "Node " + _003Ci_003E5__2, alive);
					_003C_003E1__state = 1;
					return true;
				}
				if (plc != null && plc.EditorBatchCount > 0)
				{
					_003C_003E2__current = Placements(id + "/plc", plc, alive);
					_003C_003E1__state = 2;
					return true;
				}
				break;
			}
			return false;
		}

		bool IEnumerator.MoveNext()
		{
			//ILSpy generated this explicit interface implementation from .override directive in MoveNext
			return this.MoveNext();
		}

		[DebuggerHidden]
		void IEnumerator.Reset()
		{
			throw new NotSupportedException();
		}

		[DebuggerHidden]
		IEnumerator<SceneNode> IEnumerable<SceneNode>.GetEnumerator()
		{
			_003CCellChildren_003Ed__13 _003CCellChildren_003Ed__;
			if (_003C_003E1__state == -2 && _003C_003El__initialThreadId == Environment.CurrentManagedThreadId)
			{
				_003C_003E1__state = 0;
				_003CCellChildren_003Ed__ = this;
			}
			else
			{
				_003CCellChildren_003Ed__ = new _003CCellChildren_003Ed__13(0);
			}
			_003CCellChildren_003Ed__.id = _003C_003E3__id;
			_003CCellChildren_003Ed__.nodes = _003C_003E3__nodes;
			_003CCellChildren_003Ed__.plc = _003C_003E3__plc;
			_003CCellChildren_003Ed__.alive = _003C_003E3__alive;
			return _003CCellChildren_003Ed__;
		}

		[DebuggerHidden]
		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable<SceneNode>)this).GetEnumerator();
		}
	}

	[CompilerGenerated]
	private sealed class _003CEnvironmentChildren_003Ed__10 : IEnumerable<SceneNode>, IEnumerable, IEnumerator<SceneNode>, IEnumerator, IDisposable
	{
		private int _003C_003E1__state;

		private SceneNode _003C_003E2__current;

		private int _003C_003El__initialThreadId;

		private MapStreamer s;

		public MapStreamer _003C_003E3__s;

		private _003C_003Ec__DisplayClass10_0 _003C_003E8__1;

		SceneNode IEnumerator<SceneNode>.Current
		{
			[DebuggerHidden]
			get
			{
				return _003C_003E2__current;
			}
		}

		object IEnumerator.Current
		{
			[DebuggerHidden]
			get
			{
				return _003C_003E2__current;
			}
		}

		[DebuggerHidden]
		public _003CEnvironmentChildren_003Ed__10(int _003C_003E1__state)
		{
			this._003C_003E1__state = _003C_003E1__state;
			_003C_003El__initialThreadId = Environment.CurrentManagedThreadId;
		}

		[DebuggerHidden]
		void IDisposable.Dispose()
		{
			_003C_003E8__1 = null;
			_003C_003E1__state = -2;
		}

		private bool MoveNext()
		{
			switch (_003C_003E1__state)
			{
			default:
				return false;
			case 0:
				_003C_003E1__state = -1;
				_003C_003E8__1 = new _003C_003Ec__DisplayClass10_0();
				_003C_003E8__1.s = s;
				_003C_003E2__current = Water(_003C_003E8__1.s);
				_003C_003E1__state = 1;
				return true;
			case 1:
				_003C_003E1__state = -1;
				_003C_003E2__current = Toggle(Flag("g:world/env/clouds", "Clouds", "Wolkenkuppel", "MapStreamer.ShowClouds"), () => _003C_003E8__1.s.ShowClouds, delegate(bool v)
				{
					_003C_003E8__1.s.ShowClouds = v;
				});
				_003C_003E1__state = 2;
				return true;
			case 2:
				_003C_003E1__state = -1;
				_003C_003E2__current = Toggle(Flag("g:world/env/stars", "Stars / Moon", "Sternenhimmel + Mond", "MapStreamer.ShowStars"), () => _003C_003E8__1.s.ShowStars, delegate(bool v)
				{
					_003C_003E8__1.s.ShowStars = v;
				});
				_003C_003E1__state = 3;
				return true;
			case 3:
				_003C_003E1__state = -1;
				_003C_003E2__current = Toggle(Flag("g:world/env/wires", "Power wires", "Power lines from the sector .pen files", "MapStreamer.ShowPowerWires"), () => _003C_003E8__1.s.ShowPowerWires, delegate(bool v)
				{
					_003C_003E8__1.s.ShowPowerWires = v;
				});
				_003C_003E1__state = 4;
				return true;
			case 4:
				_003C_003E1__state = -1;
				return false;
			}
		}

		bool IEnumerator.MoveNext()
		{
			//ILSpy generated this explicit interface implementation from .override directive in MoveNext
			return this.MoveNext();
		}

		[DebuggerHidden]
		void IEnumerator.Reset()
		{
			throw new NotSupportedException();
		}

		[DebuggerHidden]
		IEnumerator<SceneNode> IEnumerable<SceneNode>.GetEnumerator()
		{
			_003CEnvironmentChildren_003Ed__10 _003CEnvironmentChildren_003Ed__;
			if (_003C_003E1__state == -2 && _003C_003El__initialThreadId == Environment.CurrentManagedThreadId)
			{
				_003C_003E1__state = 0;
				_003CEnvironmentChildren_003Ed__ = this;
			}
			else
			{
				_003CEnvironmentChildren_003Ed__ = new _003CEnvironmentChildren_003Ed__10(0);
			}
			_003CEnvironmentChildren_003Ed__.s = _003C_003E3__s;
			return _003CEnvironmentChildren_003Ed__;
		}

		[DebuggerHidden]
		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable<SceneNode>)this).GetEnumerator();
		}
	}

	[CompilerGenerated]
	private sealed class _003CWorldChildren_003Ed__9 : IEnumerable<SceneNode>, IEnumerable, IEnumerator<SceneNode>, IEnumerator, IDisposable
	{
		private int _003C_003E1__state;

		private SceneNode _003C_003E2__current;

		private int _003C_003El__initialThreadId;

		private MapStreamer s;

		public MapStreamer _003C_003E3__s;

		private _003C_003Ec__DisplayClass9_0 _003C_003E8__1;

		SceneNode IEnumerator<SceneNode>.Current
		{
			[DebuggerHidden]
			get
			{
				return _003C_003E2__current;
			}
		}

		object IEnumerator.Current
		{
			[DebuggerHidden]
			get
			{
				return _003C_003E2__current;
			}
		}

		[DebuggerHidden]
		public _003CWorldChildren_003Ed__9(int _003C_003E1__state)
		{
			this._003C_003E1__state = _003C_003E1__state;
			_003C_003El__initialThreadId = Environment.CurrentManagedThreadId;
		}

		[DebuggerHidden]
		void IDisposable.Dispose()
		{
			_003C_003E8__1 = null;
			_003C_003E1__state = -2;
		}

		private bool MoveNext()
		{
			switch (_003C_003E1__state)
			{
			default:
				return false;
			case 0:
				_003C_003E1__state = -1;
				_003C_003E8__1 = new _003C_003Ec__DisplayClass9_0();
				_003C_003E8__1.s = s;
				_003C_003E2__current = Toggle(Group("g:world/island", "Island LOD", () => _003C_003E8__1.s.EditorIslandPieces().Count, () => from p in _003C_003E8__1.s.EditorIslandPieces()
					select Node(p.Node, "Island " + p.Key, () => true)), () => _003C_003E8__1.s.ShowIslandLod, delegate(bool v)
				{
					_003C_003E8__1.s.ShowIslandLod = v;
				});
				_003C_003E1__state = 1;
				return true;
			case 1:
				_003C_003E1__state = -1;
				_003C_003E2__current = Toggle(Group("g:world/areas", "Areas", () => _003C_003E8__1.s.EditorAreas().Count, () => from c in _003C_003E8__1.s.EditorAreas()
					select Cell(_003C_003E8__1.s, c)), () => _003C_003E8__1.s.EditorAreas().Exists((MapStreamer.EditorCell c) => !_003C_003E8__1.s.EditorGetHidden(c)), delegate(bool v)
				{
					foreach (MapStreamer.EditorCell item in _003C_003E8__1.s.EditorAreas())
					{
						_003C_003E8__1.s.EditorSetHidden(item, !v);
					}
				});
				_003C_003E1__state = 2;
				return true;
			case 2:
				_003C_003E1__state = -1;
				_003C_003E2__current = Toggle(Group("g:world/sectors", "Sectors", () => _003C_003E8__1.s.EditorSectors().Count, () => from c in _003C_003E8__1.s.EditorSectors()
					select Cell(_003C_003E8__1.s, c)), () => _003C_003E8__1.s.EditorSectors().Exists((MapStreamer.EditorCell c) => !_003C_003E8__1.s.EditorGetHidden(c)), delegate(bool v)
				{
					foreach (MapStreamer.EditorCell item2 in _003C_003E8__1.s.EditorSectors())
					{
						_003C_003E8__1.s.EditorSetHidden(item2, !v);
					}
				});
				_003C_003E1__state = 3;
				return true;
			case 3:
				_003C_003E1__state = -1;
				_003C_003E2__current = Group("g:world/scenes", "Scenes", () => _003C_003E8__1.s.Scenes.Count, () => _003C_003E8__1.s.Scenes.ToArray().Select((MapStreamer.SceneHandle h, int i) => Scene(_003C_003E8__1.s, h, i)));
				_003C_003E1__state = 4;
				return true;
			case 4:
				_003C_003E1__state = -1;
				if (_003C_003E8__1.s.MapTiles.Count > 0)
				{
					_003C_003E2__current = Group("g:world/maptiles", "Map tiles", () => _003C_003E8__1.s.MapTiles.Count, () => from t in _003C_003E8__1.s.MapTiles.ToArray()
						select MapTile(_003C_003E8__1.s, t));
					_003C_003E1__state = 5;
					return true;
				}
				goto IL_0261;
			case 5:
				_003C_003E1__state = -1;
				goto IL_0261;
			case 6:
				{
					_003C_003E1__state = -1;
					return false;
				}
				IL_0261:
				_003C_003E2__current = Group("g:world/env", "Environment", () => 4, () => EnvironmentChildren(_003C_003E8__1.s));
				_003C_003E1__state = 6;
				return true;
			}
		}

		bool IEnumerator.MoveNext()
		{
			//ILSpy generated this explicit interface implementation from .override directive in MoveNext
			return this.MoveNext();
		}

		[DebuggerHidden]
		void IEnumerator.Reset()
		{
			throw new NotSupportedException();
		}

		[DebuggerHidden]
		IEnumerator<SceneNode> IEnumerable<SceneNode>.GetEnumerator()
		{
			_003CWorldChildren_003Ed__9 _003CWorldChildren_003Ed__;
			if (_003C_003E1__state == -2 && _003C_003El__initialThreadId == Environment.CurrentManagedThreadId)
			{
				_003C_003E1__state = 0;
				_003CWorldChildren_003Ed__ = this;
			}
			else
			{
				_003CWorldChildren_003Ed__ = new _003CWorldChildren_003Ed__9(0);
			}
			_003CWorldChildren_003Ed__.s = _003C_003E3__s;
			return _003CWorldChildren_003Ed__;
		}

		[DebuggerHidden]
		IEnumerator IEnumerable.GetEnumerator()
		{
			return ((IEnumerable<SceneNode>)this).GetEnumerator();
		}
	}

	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

	private static readonly string[] KnownBones = new string[30]
	{
		"PELVIS", "HEAD", "NECK", "SPINE", "SPINE1", "SPINE2", "SPINE3", "ROOT", "ARM_L", "ARM_R",
		"FOREARM_L", "FOREARM_R", "HAND_L", "HAND_R", "UPLEG_L", "UPLEG_R", "LEG_L", "LEG_R", "FOOT_L", "FOOT_R",
		"TOE_L", "TOE_R", "CLAVICLE_L", "CLAVICLE_R", "SHOULDER_L", "SHOULDER_R", "IK_LEFT_FOOT", "IK_RIGHT_FOOT", "IK_LEFT_HAND", "IK_RIGHT_HAND"
	};

	private static Dictionary<ulong, string>? _boneNames;

	private static readonly FieldInfo[] DescFields = typeof(MaterialDesc).GetFields(BindingFlags.Instance | BindingFlags.Public);

	private static readonly Dictionary<string, (MaterialDesc Desc, Action Apply)> _materials = new Dictionary<string, (MaterialDesc, Action)>();

	private static Vector3 Cam => SceneEditor.CameraGame?.Invoke() ?? Vector3.Zero;

	private static string N(float v, string fmt = "0.##")
	{
		return v.ToString(fmt, Inv);
	}

	private static string Nm(string? s, string fallback)
	{
		if (!string.IsNullOrEmpty(s))
		{
			return s;
		}
		return fallback;
	}

	private static Vector3 ToGame(Vector3 render)
	{
		return SceneEditor.RenderToGame?.Invoke(render) ?? render;
	}

	public static SceneNode Group(string id, string name, Func<int> count, Func<IEnumerable<SceneNode>> children, Action<InspectorBuilder>? inspect = null, Func<bool>? alive = null, string type = "Group")
	{
		string type2 = type;
		Func<int> count2 = count;
		return new SceneNode
		{
			Id = id,
			Name = name,
			Type = type2,
			CountChildren = count2,
			Children = children,
			Alive = alive,
			Inspect = (inspect ?? ((Action<InspectorBuilder>)delegate(InspectorBuilder b)
			{
				b.Section(type2);
				b.Info("Entries", count2().ToString(Inv));
			}))
		};
	}

	public static SceneNode Toggle(SceneNode n, Func<bool> get, Action<bool> set)
	{
		n.GetActive = get;
		n.SetActive = set;
		return n;
	}

	public static SceneNode World(MapStreamer s)
	{
		MapStreamer s2 = s;
		return new SceneNode
		{
			Id = "g:world",
			Name = "World " + (s2.Index?.IslandName ?? s2.Island) + "",
			Type = "MapStreamer",
			Target = s2,
			GetActive = () => !s2.HideWorld,
			SetActive = delegate(bool v)
			{
				s2.HideWorld = !v;
			},
			Pick = (Vector3 o, Vector3 d) => PickWorld(s2, o, d),
			CountChildren = () => 5 + ((s2.MapTiles.Count > 0) ? 1 : 0),
			Children = () => WorldChildren(s2),
			Inspect = delegate(InspectorBuilder b)
			{
				b.Section("MapStreamer");
				b.Info("Island", s2.Index?.IslandName ?? s2.Island);
				b.Info("Status", s2.Status);
				b.Info("Areas loaded", s2.LoadedAreas.ToString(Inv));
				b.Info("Sectors loaded", s2.LoadedSectors.ToString(Inv));
				b.Info("Visible objects", s2.DrawnObjects.ToString(Inv));
				b.Info("Camera", s2.ViewerGame);
				b.Info("Render origin", s2.RootOffset);
				b.Info("Water level", s2.WaterLevel);
				b.Info("Active toggle", "off = hide the outside world");
			}
		};
	}

	[IteratorStateMachine(typeof(_003CWorldChildren_003Ed__9))]
	private static IEnumerable<SceneNode> WorldChildren(MapStreamer s)
	{
		//yield-return decompiler failed: Unexpected instruction in Iterator.Dispose()
		return new _003CWorldChildren_003Ed__9(-2)
		{
			_003C_003E3__s = s
		};
	}

	[IteratorStateMachine(typeof(_003CEnvironmentChildren_003Ed__10))]
	private static IEnumerable<SceneNode> EnvironmentChildren(MapStreamer s)
	{
		//yield-return decompiler failed: Unexpected instruction in Iterator.Dispose()
		return new _003CEnvironmentChildren_003Ed__10(-2)
		{
			_003C_003E3__s = s
		};
	}

	private static SceneNode Flag(string id, string name, string what, string field)
	{
		string name2 = name;
		string what2 = what;
		string field2 = field;
		return new SceneNode
		{
			Id = id,
			Name = name2,
			Type = what2,
			Inspect = delegate(InspectorBuilder b)
			{
				b.Section(name2);
				b.Info("Inhalt", what2);
				b.Info("Schalter", field2);
			}
		};
	}

	private static SceneNode Cell(MapStreamer s, MapStreamer.EditorCell c)
	{
		MapStreamer s2 = s;
		MapStreamer.EditorCell c2 = c;
		string id = "g:" + c2.Kind + "/" + c2.Key;
		Func<bool> alive = () => s2.EditorIsLoaded(c2);
		IReadOnlyList<MapStreamer.Node> nodes = c2.Nodes;
		PlacementRenderer plc = c2.Placements;
		int plcCount = ((plc != null && plc.EditorBatchCount > 0) ? 1 : 0);
		return new SceneNode
		{
			Id = id,
			Name = c2.Kind + "" + c2.Key + (c2.Failed ? "" : (c2.Built ? "" : "")),
			Type = c2.Kind,
			Alive = alive,
			GetActive = () => !s2.EditorGetHidden(c2),
			SetActive = delegate(bool v)
			{
				s2.EditorSetHidden(c2, !v);
			},
			CountChildren = () => nodes.Count + plcCount,
			Children = () => CellChildren(id, nodes, plc, alive),
			Inspect = delegate(InspectorBuilder b)
			{
				b.Section(c2.Kind);
				b.Info("Key", c2.Key);
				b.Info("Bank", c2.Bank);
				b.Info("Gebaut", c2.Built ? "yes" : "no");
				if (c2.Failed)
				{
					b.Info("Error", "Load failed");
				}
				b.Info("Nodes", nodes.Count.ToString(Inv));
				b.Info("Objects", nodes.Sum((MapStreamer.Node n) => n.Objects.Count).ToString(Inv));
				b.Info("Additional banks", c2.ExtraModels.ToString(Inv));
				b.Info("Colliders", c2.Colliders.ToString(Inv));
				if (plc != null)
				{
					b.Info("Platzierungen", plc.EditorBatchCount + " Typen, " + plc.InstanceCount + " Instanzen");
				}
				b.Info("Gras", c2.HasGrass ? "yes" : "no");
				b.Info("Stromleitungen", c2.HasWires ? "yes" : "no");
				b.Info("Active toggle", "off = do not draw the cell");
			},
			Gizmo = delegate(SceneGizmo g)
			{
				Vector3 vector = new Vector3(float.MaxValue);
				Vector3 vector2 = new Vector3(float.MinValue);
				foreach (MapStreamer.Node item in nodes)
				{
					if (item.HasBounds)
					{
						vector = Vector3.Min(vector, item.BoundsMin);
						vector2 = Vector3.Max(vector2, item.BoundsMax);
					}
				}
				g.Aabb(vector, vector2);
			}
		};
	}

	[IteratorStateMachine(typeof(_003CCellChildren_003Ed__13))]
	private static IEnumerable<SceneNode> CellChildren(string id, IReadOnlyList<MapStreamer.Node> nodes, PlacementRenderer? plc, Func<bool> alive)
	{
		//yield-return decompiler failed: Unexpected instruction in Iterator.Dispose()
		return new _003CCellChildren_003Ed__13(-2)
		{
			_003C_003E3__id = id,
			_003C_003E3__nodes = nodes,
			_003C_003E3__plc = plc,
			_003C_003E3__alive = alive
		};
	}

	public static SceneNode Node(MapStreamer.Node n, string fallbackName, Func<bool> cellAlive)
	{
		MapStreamer.Node i = n;
		Func<bool> cellAlive2 = cellAlive;
		Func<bool> alive = () => !i.Disposed && cellAlive2();
		SceneNode sceneNode = new SceneNode();
		sceneNode.Id = EditorIds.Of(i);
		sceneNode.Name = Nm(i.Name, fallbackName);
		sceneNode.Type = "Node " + i.Objects.Count + " objects" + ((i.Meshes.Count > 0) ? (", " + i.Meshes.Count + " Meshes") : "") + "";
		sceneNode.Target = i;
		sceneNode.GetActive = () => i.Active;
		sceneNode.SetActive = delegate(bool v)
		{
			i.Active = v;
		};
		sceneNode.Alive = alive;
		sceneNode.CountChildren = () => i.Objects.Count;
		sceneNode.Children = () => from o in i.Objects.ToArray()
			select RenderObject(o, alive);
		sceneNode.Inspect = delegate(InspectorBuilder b)
		{
			b.Section("Node");
			b.Text("Name", i.Name, "name");
			b.Info("Objects", i.Objects.Count.ToString(Inv));
			b.Info("Eigene Meshes", i.Meshes.Count.ToString(Inv));
			if (i.HasBounds)
			{
				Bounds(b, i.BoundsMin, i.BoundsMax);
			}
			if (i.Meshes.Count > 0)
			{
				b.Section("Mesh Renderer");
				for (int j = 0; j < i.Meshes.Count && j < 64; j++)
				{
					var (m2, m3, matrix4x) = i.Meshes[j];
					b.Info("Mesh " + j, Tris(m2) + ", " + MatName(m3) + ", Pos " + InspectorBuilder.V3(matrix4x.Translation, "0.#"));
				}
				Materials(b, i.Meshes.Select<(IGpuMesh, IGpuMaterial, Matrix4x4), IGpuMaterial>(((IGpuMesh Mesh, IGpuMaterial Material, Matrix4x4 Model) m) => m.Material));
			}
		};
		sceneNode.Edit = delegate(string k, string v)
		{
			if (k == "name")
			{
				i.Name = v;
				return true;
			}
			if (k == "active")
			{
				i.Active = v == "1";
				return true;
			}
			return false;
		};
		sceneNode.Gizmo = delegate(SceneGizmo g)
		{
			if (i.HasBounds)
			{
				g.Aabb(i.BoundsMin, i.BoundsMax);
			}
		};
		return sceneNode;
	}

	public static SceneNode RenderObject(RenderObject ro, Func<bool> alive)
	{
		RenderObject ro2 = ro;
		Func<bool> alive2 = alive;
		string id = EditorIds.Of(ro2);
		return new SceneNode
		{
			Id = id,
			Name = Nm(ro2.Name, ""),
			Type = "RenderObject " + ro2.Lods.Length + " LOD",
			Target = ro2,
			GetActive = () => ro2.Active,
			SetActive = delegate(bool v)
			{
				ro2.Active = v;
			},
			Alive = alive2,
			CountChildren = () => (ro2.Lods.Length > 1) ? ro2.Lods.Length : 0,
			Children = () => from i in Enumerable.Range(0, ro2.Lods.Length)
				select Lod(id, ro2, i, alive2),
			Inspect = delegate(InspectorBuilder b)
			{
				InspectRenderObject(b, ro2);
			},
			Edit = (string k, string v) => EditRenderObject(ro2, k, v),
			Gizmo = delegate(SceneGizmo g)
			{
				g.Aabb(ro2.BoundsMin, ro2.BoundsMax);
			}
		};
	}

	private static void InspectRenderObject(InspectorBuilder b, RenderObject ro)
	{
		b.Section("RenderObject");
		b.Text("Name", ro.Name, "name");
		b.Section("Transform");
		if (TransformMath.Decompose(ro.Model, out var pos, out var rot, out var scale))
		{
			b.Vec3("Position", pos, "pos");
			b.Vec3("Rotation", TransformMath.ToEuler(rot), "rot");
			b.Vec3("Scale", scale, "scale");
			if (ro.Model.GetDeterminant() < 0f)
			{
				b.Info("Gespiegelt", "yes");
			}
		}
		else
		{
			b.Info("Matrix", "not decomposable");
			b.Info("Position", ro.Model.Translation);
		}
		b.Info("Note", "Collision stays at the old position when moving");
		Bounds(b, ro.BoundsMin, ro.BoundsMax);
		b.Info("Center", ro.Center);
		b.Section("LOD Group");
		Vector3 cam = Cam;
		float v = Vector3.Distance(cam, ro.Center);
		RenderLod renderLod = ro.SelectLod(cam);
		b.Info("Camera distance", N(v, "0.0") + " m");
		b.Info("Aktuelle LOD", (renderLod == null) ? "none" : ("LOD " + Array.IndexOf(ro.Lods, renderLod)));
		b.Info("Hide last LOD", ro.CullLastLod ? "yes" : "no");
		b.Info("LOD-Bias", N(Paradise.Core.World.RenderObject.LodBias));
		for (int i = 0; i < ro.Lods.Length; i++)
		{
			RenderLod renderLod2 = ro.Lods[i];
			b.Info("LOD " + i, (float.IsPositiveInfinity(renderLod2.MaxDistance) ? "unbegrenzt" : ("to " + N(renderLod2.MaxDistance * Paradise.Core.World.RenderObject.LodBias, "0") + " m")) + ", " + renderLod2.Parts.Length + " Teile, " + TrisSum(renderLod2.Parts));
		}
		if (ro.Lods.Length != 0)
		{
			b.Section("Mesh Renderer");
			Parts(b, ro.Lods[0].Parts);
		}
		Materials(b, from p in ro.Lods.SelectMany((RenderLod l) => l.Parts)
			select p.Material);
	}

	private static bool EditRenderObject(RenderObject ro, string k, string v)
	{
		switch (k)
		{
		case "name":
			ro.Name = v;
			return true;
		case "active":
			ro.Active = v == "1";
			return true;
		case "pos":
		case "rot":
		case "scale":
		{
			if (!InspectorBuilder.TryParseV3(v, out var v2))
			{
				return false;
			}
			Matrix4x4 model = ro.Model;
			if (!TransformMath.Decompose(model, out var pos, out var rot, out var scale))
			{
				return false;
			}
			if (k == "pos")
			{
				pos = v2;
			}
			else if (k == "rot")
			{
				rot = TransformMath.FromEuler(v2);
			}
			else
			{
				scale = v2;
			}
			Matrix4x4 matrix4x = TransformMath.Compose(pos, rot, scale);
			if (!Matrix4x4.Invert(model, out var result))
			{
				return false;
			}
			Matrix4x4 matrix4x2 = result * matrix4x;
			TransformMath.TransformAabb(ro.BoundsMin, ro.BoundsMax, matrix4x2, out var outMin, out var outMax);
			ro.BoundsMin = outMin;
			ro.BoundsMax = outMax;
			ro.Center = Vector3.Transform(ro.Center, matrix4x2);
			ro.Model = matrix4x;
			return true;
		}
		default:
			return false;
		}
	}

	private static SceneNode Lod(string ownerId, RenderObject ro, int i, Func<bool> alive)
	{
		RenderObject ro2 = ro;
		RenderLod j = ro2.Lods[i];
		return new SceneNode
		{
			Id = ownerId + "/lod" + i,
			Name = "LOD " + i,
			Type = "LOD level",
			Alive = alive,
			Inspect = delegate(InspectorBuilder b)
			{
				b.Section("LOD " + i + " of " + Nm(ro2.Name, ""));
				b.Info("Sichtweite", float.IsPositiveInfinity(j.MaxDistance) ? "unbegrenzt" : ("to " + N(j.MaxDistance * Paradise.Core.World.RenderObject.LodBias, "0") + " m"));
				b.Info("Active", (ro2.SelectLod(Cam) == j) ? "yes" : "no");
				b.Section("Mesh Renderer");
				Parts(b, j.Parts);
				Materials(b, j.Parts.Select((RenderPart p) => p.Material));
			},
			Gizmo = delegate(SceneGizmo g)
			{
				g.Aabb(ro2.BoundsMin, ro2.BoundsMax);
			}
		};
	}

	private static SceneNode Placements(string id, PlacementRenderer p, Func<bool> alive)
	{
		PlacementRenderer p2 = p;
		string id2 = id;
		Func<bool> alive2 = alive;
		return Toggle(Group(id2, "Placements " + p2.EditorBatchCount + " Typen, " + p2.InstanceCount + " Instanzen", () => p2.EditorBatchCount, () => from i in Enumerable.Range(0, p2.EditorBatchCount)
			select PlacementBatch(id2 + "/" + i, p2, i, alive2), delegate(InspectorBuilder b)
		{
			b.Section("PlacementRenderer");
			b.Info("Typen", p2.EditorBatchCount.ToString(Inv));
			b.Info("Instanzen", p2.InstanceCount.ToString(Inv));
			b.Info("Sichtweite", N(p2.MaxDistance * PlacementRenderer.DistanceMultiplier, "0") + " m");
		}, alive2, "Placements"), () => !p2.EditorHidden, delegate(bool v)
		{
			p2.EditorHidden = !v;
		});
	}

	private static SceneNode PlacementBatch(string id, PlacementRenderer p, int batch, Func<bool> alive)
	{
		PlacementRenderer p2 = p;
		string id2 = id;
		Func<bool> alive2 = alive;
		LibraryObject obj = p2.EditorBatchObject(batch);
		IReadOnlyList<Matrix4x4> mats = p2.EditorBatchMatrices(batch);
		return new SceneNode
		{
			Id = id2,
			Name = Nm(obj.Name, "") + " ×" + mats.Count,
			Type = "Library-Objekt",
			Target = obj,
			Alive = alive2,
			GetActive = () => !p2.EditorBatchHidden(batch),
			SetActive = delegate(bool v)
			{
				p2.EditorSetBatchHidden(batch, !v);
			},
			CountChildren = () => mats.Count,
			Children = () => from j in Enumerable.Range(0, mats.Count)
				select PlacementInstance(id2 + "#" + j, p2, batch, j, alive2),
			Inspect = delegate(InspectorBuilder b)
			{
				InspectLibrary(b, obj);
				b.Section("Instanzen");
				b.Info("Count", mats.Count.ToString(Inv));
			},
			Gizmo = delegate(SceneGizmo g)
			{
				for (int i = 0; i < mats.Count && i < 256; i++)
				{
					g.Box(obj.BoundsMin, obj.BoundsMax, mats[i], i > 0);
				}
			}
		};
	}

	private static SceneNode PlacementInstance(string id, PlacementRenderer p, int batch, int j, Func<bool> alive)
	{
		PlacementRenderer p2 = p;
		LibraryObject obj = p2.EditorBatchObject(batch);
		IReadOnlyList<Matrix4x4> mats = p2.EditorBatchMatrices(batch);
		return new SceneNode
		{
			Id = id,
			Name = Nm(obj.Name, "") + " #" + j,
			Type = "Platzierte Instanz",
			Alive = alive,
			GetActive = () => !p2.EditorInstanceHidden(batch, j),
			SetActive = delegate(bool v)
			{
				p2.EditorSetInstanceHidden(batch, j, !v);
			},
			Inspect = delegate(InspectorBuilder b)
			{
				Matrix4x4 m = mats[j];
				b.Section("Transform");
				if (TransformMath.Decompose(m, out var pos, out var rot, out var scale))
				{
					b.Info("Position", pos);
					b.Info("Rotation", TransformMath.ToEuler(rot), "0.##");
					b.Info("Scale", scale);
				}
				else
				{
					b.Info("Position", m.Translation);
				}
				b.Info("Note", "Instances are pre-sorted – cannot be changed here");
				TransformMath.TransformAabb(obj.BoundsMin, obj.BoundsMax, m, out var outMin, out var outMax);
				Bounds(b, outMin, outMax);
				InspectLibrary(b, obj);
			},
			Gizmo = delegate(SceneGizmo g)
			{
				g.Box(obj.BoundsMin, obj.BoundsMax, mats[j]);
			}
		};
	}

	private static void InspectLibrary(InspectorBuilder b, LibraryObject obj)
	{
		b.Section("Library-Objekt");
		b.Info("Name", obj.Name);
		b.Info("Key", "0x" + obj.Key.ToString("x16", Inv));
		b.Info("Radius", obj.Radius);
		b.Info("Local bounds", InspectorBuilder.V3(obj.BoundsMin) + " … " + InspectorBuilder.V3(obj.BoundsMax));
		b.Section("LOD Group");
		for (int i = 0; i < obj.Lods.Length; i++)
		{
			b.Info("LOD " + i, "to " + N(obj.Lods[i].MaxDistance, "0") + " m, " + obj.Lods[i].Parts.Length + " Teile, " + TrisSum(obj.Lods[i].Parts) + ", Geo 0x" + obj.Lods[i].GeoKey.ToString("x16", Inv));
		}
		if (obj.Lods.Length != 0)
		{
			b.Section("Mesh Renderer");
			Parts(b, obj.Lods[0].Parts);
		}
		Materials(b, from p in obj.Lods.SelectMany((LibraryLod l) => l.Parts)
			select p.Material);
	}

	private static SceneNode Scene(MapStreamer s, MapStreamer.SceneHandle h, int index)
	{
		MapStreamer s2 = s;
		MapStreamer.SceneHandle h2 = h;
		Func<bool> alive = () => s2.Scenes.Contains(h2);
		string bank = h2.Model?.Path ?? "";
		return new SceneNode
		{
			Id = EditorIds.Of(h2),
			Name = ((bank.Length > 0) ? Path.GetFileName(bank) : ("Scene " + index)),
			Type = "SceneHandle " + h2.Objects.Count + " Objekte",
			Target = h2,
			GetActive = () => h2.Active,
			SetActive = delegate(bool v)
			{
				h2.Active = v;
			},
			Alive = alive,
			CountChildren = () => h2.Objects.Count,
			Children = () => from o in h2.Objects.ToArray()
				select RenderObject(o, alive),
			Inspect = delegate(InspectorBuilder b)
			{
				b.Section("Scene");
				b.Info("Bank", bank);
				b.Info("Objects", h2.Objects.Count.ToString(Inv));
				Bounds(b, h2.BoundsMin, h2.BoundsMax);
			},
			Edit = delegate(string k, string v)
			{
				if (k != "active")
				{
					return false;
				}
				h2.Active = v == "1";
				return true;
			},
			Gizmo = delegate(SceneGizmo g)
			{
				g.Aabb(h2.BoundsMin, h2.BoundsMax);
			}
		};
	}

	private static SceneNode MapTile(MapStreamer s, MapStreamer.MapTile t)
	{
		MapStreamer s2 = s;
		MapStreamer.MapTile t2 = t;
		return new SceneNode
		{
			Id = EditorIds.Of(t2),
			Name = "Map tile " + t2.Key,
			Type = "Kartenkachel",
			Alive = () => s2.MapTiles.Contains(t2),
			Inspect = delegate(InspectorBuilder b)
			{
				b.Section("Map tile");
				b.Info("Key", t2.Key.ToString());
				b.Info("Origin", t2.Origin);
				b.Info("Mesh", (t2.Mesh == null) ? "-" : Tris(t2.Mesh));
				b.Info("Farbtextur", Tex(t2.Color));
				if (t2.Material != null)
				{
					Materials(b, new IGpuMaterial[1] { t2.Material });
				}
			}
		};
	}

	private static SceneNode Water(MapStreamer s)
	{
		MapStreamer s2 = s;
		return new SceneNode
		{
			Id = "g:world/water",
			Name = "Water",
			Type = "Water surface",
			GetActive = () => !s2.EditorHideWater,
			SetActive = delegate(bool v)
			{
				s2.EditorHideWater = !v;
			},
			Inspect = delegate(InspectorBuilder b)
			{
				(IGpuMesh, IGpuMaterial, Vector3) editorWater = s2.EditorWater;
				b.Section("Meer");
				b.Info("Position", editorWater.Item3);
				b.Info("Water level", s2.WaterLevel);
				b.Info("Island water level", s2.IslandWaterHeight);
				b.Bool("Created on load", s2.ShowWater);
				b.Info("Mesh", (editorWater.Item1 == null) ? "-" : Tris(editorWater.Item1));
				if (editorWater.Item2 != null)
				{
					Materials(b, new IGpuMaterial[1] { editorWater.Item2 });
				}
			}
		};
	}

	public static SceneNode Car(string id, string name, string type, CarModel model, Func<bool> alive, Action<InspectorBuilder>? extra = null)
	{
		Func<bool> alive2 = alive;
		Action<InspectorBuilder> extra2 = extra;
		CarModel model2 = model;
		string id2 = id;
		CarNode v = model2.Visual;
		return new SceneNode
		{
			Id = id2,
			Name = name,
			Type = type,
			Target = model2,
			GetActive = () => v.Active,
			SetActive = delegate(bool a)
			{
				v.Active = a;
			},
			Alive = alive2,
			CountChildren = () => v.Children.Count,
			Children = () => from c in v.Children.ToArray()
				select CarNode(c, alive2),
			Inspect = delegate(InspectorBuilder b)
			{
				extra2?.Invoke(b);
				b.Section("Fahrzeugmodell");
				b.Info("Position", ToGame(v.World.Translation));
				b.Info("Karosserie Min", model2.BodyMin);
				b.Info("Karosserie Max", model2.BodyMax);
				b.Info("Body size", model2.BodySize);
				b.Info("Reifenradius vorne / hinten", N(model2.TireRadiusFront, "0.###") + " / " + N(model2.TireRadiusRear, "0.###") + " m");
				b.Info("Nodes", model2.Nodes.Count.ToString(Inv));
				b.Info("Materialien", model2.AllMaterials.Count.ToString(Inv));
				b.Section("All materials", collapsed: true);
				foreach (CarMaterial allMaterial in model2.AllMaterials)
				{
					b.Info(allMaterial.Name, allMaterial.Desc.Kind.ToString() + ((allMaterial.Gpu == null) ? "" : ""));
				}
			},
			Edit = delegate(string k, string val)
			{
				if (k != "active")
				{
					return false;
				}
				v.Active = val == "1";
				return true;
			},
			Gizmo = delegate(SceneGizmo g)
			{
				g.BoxRender(model2.BodyMin, model2.BodyMax, v.World);
			},
			Pick = (Vector3 o, Vector3 d) => PickCar(id2, model2, o, d, alive2)
		};
	}

	private static PickHit? PickCar(string rootId, CarModel model, Vector3 o, Vector3 d, Func<bool> alive)
	{
		CarNode visual = model.Visual;
		if (!visual.Active || !RayObb(o, d, model.BodyMin, model.BodyMax, visual.World, out var t, out var volume))
		{
			return null;
		}
		PickHit pickHit = null;
		CarNode carNode = null;
		foreach (CarNode item in visual.SelfAndDescendants())
		{
			if (item != visual && item.HasMesh && item.RendererEnabled && item.ActiveInHierarchy && RayObb(o, d, item.MeshMin, item.MeshMax, item.World, out var t2, out var volume2))
			{
				PickHit pickHit2 = new PickHit
				{
					T = t2,
					Volume = volume2
				};
				if (PickHit.Better(pickHit2, pickHit))
				{
					pickHit = pickHit2;
					carNode = item;
				}
			}
		}
		if (pickHit == null || carNode == null)
		{
			return new PickHit
			{
				Node = Car(rootId, "", "", model, alive),
				T = t,
				Volume = volume
			};
		}
		List<string> list = new List<string> { rootId };
		List<string> list2 = new List<string>();
		CarNode parent = carNode.Parent;
		while (parent != null && parent != visual)
		{
			list2.Insert(0, EditorIds.Of(parent));
			parent = parent.Parent;
		}
		list.AddRange(list2);
		pickHit.Node = CarNode(carNode, alive);
		pickHit.Path = list.ToArray();
		return pickHit;
	}

	private static bool RayObb(Vector3 o, Vector3 d, Vector3 min, Vector3 max, Matrix4x4 world, out float t, out float volume)
	{
		t = 0f;
		volume = 0f;
		if (!(min.X <= max.X) || !Matrix4x4.Invert(world, out var result))
		{
			return false;
		}
		if (!MapStreamer.EditorRayBox(Vector3.Transform(o, result), Vector3.TransformNormal(d, result), min, max, out t))
		{
			return false;
		}
		Vector3 vector = (max - min) * new Vector3(new Vector3(world.M11, world.M12, world.M13).Length(), new Vector3(world.M21, world.M22, world.M23).Length(), new Vector3(world.M31, world.M32, world.M33).Length());
		volume = MathF.Max(vector.X, 0.01f) * MathF.Max(vector.Y, 0.01f) * MathF.Max(vector.Z, 0.01f);
		return true;
	}

	private static PickHit? PickWorld(MapStreamer s, Vector3 originRender, Vector3 dir)
	{
		MapStreamer s2 = s;
		List<MapStreamer.EditorPickHit> list = s2.EditorPick(s2.RenderToGame(originRender), dir);
		if (list.Count == 0)
		{
			return null;
		}
		MapStreamer.EditorPickHit editorPickHit = list[0];
		SceneNode node;
		string[] path;
		if (editorPickHit.Scene != null && editorPickHit.Object != null)
		{
			MapStreamer.SceneHandle sc = editorPickHit.Scene;
			Func<bool> alive = () => s2.Scenes.Contains(sc);
			node = RenderObject(editorPickHit.Object, alive);
			path = new string[3]
			{
				"g:world",
				"g:world/scenes",
				EditorIds.Of(sc)
			};
		}
		else
		{
			if (editorPickHit.Cell == null)
			{
				return null;
			}
			MapStreamer.EditorCell c = editorPickHit.Cell;
			string text = "g:" + c.Kind + "/" + c.Key;
			string text2 = ((c.Kind == "Area") ? "g:world/areas" : "g:world/sectors");
			Func<bool> cellAlive = () => s2.EditorIsLoaded(c);
			if (editorPickHit.Object != null && editorPickHit.Node != null)
			{
				MapStreamer.Node i = editorPickHit.Node;
				node = RenderObject(editorPickHit.Object, () => !i.Disposed && cellAlive());
				path = new string[4]
				{
					"g:world",
					text2,
					text,
					EditorIds.Of(i)
				};
			}
			else
			{
				if (c.Placements == null || editorPickHit.Batch < 0)
				{
					return null;
				}
				string text3 = text + "/plc";
				string text4 = text3 + "/" + editorPickHit.Batch;
				node = PlacementInstance(text4 + "#" + editorPickHit.Instance, c.Placements, editorPickHit.Batch, editorPickHit.Instance, cellAlive);
				path = new string[5] { "g:world", text2, text, text3, text4 };
			}
		}
		return new PickHit
		{
			Node = node,
			Path = path,
			T = editorPickHit.T,
			Volume = editorPickHit.Volume
		};
	}

	public static SceneNode CarNode(CarNode n, Func<bool> alive)
	{
		CarNode i = n;
		Func<bool> alive2 = alive;
		return new SceneNode
		{
			Id = EditorIds.Of(i),
			Name = Nm(i.Name, ""),
			Type = "CarNode" + (i.HasMesh ? (" + Mesh " + i.Parts.Count + " Teile") : ""),
			Target = i,
			GetActive = () => i.Active,
			SetActive = delegate(bool v)
			{
				i.Active = v;
			},
			Alive = alive2,
			CountChildren = () => i.Children.Count,
			Children = () => from c in i.Children.ToArray()
				select CarNode(c, alive2),
			Inspect = delegate(InspectorBuilder b)
			{
				InspectCarNode(b, i);
			},
			Edit = (string k, string v) => EditCarNode(i, k, v),
			Gizmo = delegate(SceneGizmo g)
			{
				if (i.HasMesh)
				{
					g.BoxRender(i.MeshMin, i.MeshMax, i.World);
				}
				else
				{
					g.AxesRender(i.World, 0.3f);
				}
			}
		};
	}

	private static void InspectCarNode(InspectorBuilder b, CarNode n)
	{
		b.Section("CarNode");
		b.Text("Name", n.Name, "name");
		b.Info("Active in hierarchy", n.ActiveInHierarchy ? "yes" : "no");
		b.Info("Eltern", (n.Parent == null) ? "-" : Nm(n.Parent.Name, ""));
		b.Section("Transform");
		b.Vec3("Position", n.LocalPosition, "pos");
		b.Vec3("Rotation", TransformMath.ToEuler(n.LocalRotation), "rot");
		b.Vec3("Scale", n.LocalScale, "scale");
		if (n.RawLocal.HasValue)
		{
			b.Info("Note", "Matrix from the file – changing replaces it");
		}
		b.Info("Welt-Position", ToGame(n.World.Translation));
		b.Info("Animation note", "The game resets wheels/steering/doors every frame");
		b.Section("Mesh Renderer");
		b.Bool("Renderer an", n.RendererEnabled, "renderer");
		b.Info("Has mesh", n.HasMesh ? "yes" : "no");
		if (n.HasMesh)
		{
			b.Info("Mesh-Bounds", InspectorBuilder.V3(n.MeshMin) + " … " + InspectorBuilder.V3(n.MeshMax));
		}
		for (int i = 0; i < n.Parts.Count; i++)
		{
			CarPart carPart = n.Parts[i];
			b.Info("Teil " + i, "Submesh " + carPart.SubMesh + ", " + Tris(carPart.Mesh) + ", " + Nm(carPart.Material.Name, "?"));
		}
		foreach (CarMaterial item in n.Parts.Select((CarPart p) => p.Material).Distinct())
		{
			CarMaterialSection(b, item);
		}
	}

	private static bool EditCarNode(CarNode n, string k, string v)
	{
		switch (k)
		{
		case "name":
			n.Name = v;
			return true;
		case "active":
			n.Active = v == "1";
			return true;
		case "renderer":
			n.RendererEnabled = v == "1";
			return true;
		case "pos":
		case "rot":
		case "scale":
		{
			if (!InspectorBuilder.TryParseV3(v, out var v2))
			{
				return false;
			}
			if (n.RawLocal.HasValue)
			{
				if (TransformMath.Decompose(n.RawLocal.Value, out var pos, out var rot, out var scale))
				{
					n.LocalPosition = pos;
					n.LocalRotation = rot;
					n.LocalScale = scale;
				}
				n.RawLocal = null;
			}
			if (k == "pos")
			{
				n.LocalPosition = v2;
			}
			else if (k == "rot")
			{
				n.LocalRotation = TransformMath.FromEuler(v2);
			}
			else
			{
				n.LocalScale = v2;
			}
			return true;
		}
		default:
			return false;
		}
	}

	private static void CarMaterialSection(InspectorBuilder b, CarMaterial m)
	{
		CarMaterial i = m;
		b.Section("Material: " + Nm(i.Name, "Car"));
		b.Info("Shader", i.Desc.Kind.ToString());
		b.Info("Auf der GPU", (i.Gpu != null) ? "yes" : "no");
		TextureRows(b, i.Desc);
		string matId = RegisterMaterial(i, i.Desc, delegate
		{
			IRenderer renderer = MaterialFactory.Renderer;
			if (renderer != null)
			{
				i.Apply(renderer);
			}
		});
		DescSection(b, Nm(i.Name, "Car"), i.Desc, matId);
	}

	public static SceneNode Avatars(Func<AvatarModel, (string Name, string Type)?>? describe = null)
	{
		Func<AvatarModel, (string Name, string Type)?> describe2 = describe;
		return Toggle(Group("g:avatars", "Avatars", () => AvatarModel.EditorAll().Count, () => AvatarModel.EditorAll().Select(delegate(AvatarModel a, int i)
		{
			(string, string) tuple = describe2?.Invoke(a) ?? DescribeAvatar(a);
			return Avatar(a, tuple.Item1 + " #" + (i + 1).ToString(Inv), tuple.Item2);
		}), delegate(InspectorBuilder b)
		{
			List<AvatarModel> list = AvatarModel.EditorAll();
			b.Section("Avatare");
			b.Info("Count", list.Count.ToString(Inv));
			b.Info("Bereit", list.Count((AvatarModel a) => a.Ready).ToString(Inv));
			b.Info("Gerade gezeichnet", list.Count((AvatarModel a) => Drawn(a)).ToString(Inv));
		}, null, "Avatare"), () => AvatarModel.EditorAll().Exists((AvatarModel a) => !a.EditorHidden), delegate(bool v)
		{
			foreach (AvatarModel item in AvatarModel.EditorAll())
			{
				item.EditorHidden = !v;
			}
		});
	}

	private static (string Name, string Type) DescribeAvatar(AvatarModel a)
	{
		foreach (CarAvatar item in CarAvatar.All)
		{
			if (item.Avatar == a)
			{
				return (Name: item.Passenger ? "Beifahrer" : "Fahrer", Type: "CarAvatar");
			}
		}
		return (Name: "Avatar", Type: "AvatarModel");
	}

	private static bool Drawn(AvatarModel a)
	{
		if (a.EditorDrawnTick > 0)
		{
			return Environment.TickCount64 - a.EditorDrawnTick < 1000;
		}
		return false;
	}

	public static SceneNode Avatar(AvatarModel a, string name, string type)
	{
		AvatarModel a2 = a;
		string name2 = name;
		string type2 = type;
		string id = EditorIds.Of(a2);
		Func<bool> alive = () => !a2.EditorDisposed;
		return new SceneNode
		{
			Id = id,
			Name = name2,
			Type = type2,
			Target = a2,
			Alive = alive,
			GetActive = () => !a2.EditorHidden,
			SetActive = delegate(bool v)
			{
				a2.EditorHidden = !v;
			},
			CountChildren = () => (a2.Rig != null) ? ChildBones(a2.Rig.Skeleton, -1).Count : 0,
			Children = () => (a2.Rig != null) ? (from i in ChildBones(a2.Rig.Skeleton, -1)
				select Bone(id, a2, i, alive)) : Enumerable.Empty<SceneNode>(),
			Inspect = delegate(InspectorBuilder b)
			{
				InspectAvatar(b, a2);
			},
			Gizmo = delegate(SceneGizmo g)
			{
				if (AvatarBox(a2, out var min2, out var max2))
				{
					g.BoxRender(min2, max2, a2.EditorLastModel);
				}
			},
			Pick = delegate(Vector3 o, Vector3 d)
			{
				if (a2.EditorHidden || !Drawn(a2) || !AvatarBox(a2, out var min, out var max))
				{
					return (PickHit?)null;
				}
				float t;
				float volume;
				return (!RayObb(o, d, min, max, a2.EditorLastModel, out t, out volume)) ? null : new PickHit
				{
					Node = Avatar(a2, name2, type2),
					T = t,
					Volume = volume
				};
			}
		};
	}

	private static bool AvatarBox(AvatarModel a, out Vector3 min, out Vector3 max)
	{
		min = new Vector3(float.MaxValue);
		max = new Vector3(float.MinValue);
		AvatarRig rig = a.Rig;
		if (rig == null || !a.Ready)
		{
			return false;
		}
		int count = rig.Skeleton.Count;
		for (int i = 0; i < count; i++)
		{
			Vector3 value = rig.BonePosition(i);
			min = Vector3.Min(min, value);
			max = Vector3.Max(max, value);
		}
		if (!(min.X <= max.X))
		{
			return false;
		}
		min -= new Vector3(0.12f);
		max += new Vector3(0.12f);
		return true;
	}

	private static List<int> ChildBones(XsbSkeleton sk, int parent)
	{
		List<int> list = new List<int>();
		for (int i = 0; i < sk.Count; i++)
		{
			int num = ((i < sk.Parent.Length) ? sk.Parent[i] : (-1));
			bool flag = num < 0 || num >= sk.Count || num == i;
			bool num2;
			if (parent >= 0)
			{
				if (num != parent)
				{
					continue;
				}
				num2 = !flag;
			}
			else
			{
				num2 = flag;
			}
			if (num2)
			{
				list.Add(i);
			}
		}
		return list;
	}

	private static string BoneName(XsbSkeleton sk, int i)
	{
		if (_boneNames == null)
		{
			_boneNames = new Dictionary<ulong, string>();
			string[] knownBones = KnownBones;
			foreach (string text in knownBones)
			{
				_boneNames.TryAdd(MaterialDef.KeyFromName(text), text);
			}
		}
		ulong key = ((i < sk.Key.Length) ? sk.Key[i] : 0);
		if (!_boneNames.TryGetValue(key, out string value))
		{
			return "Bone " + i.ToString(Inv) + "" + key.ToString("x16", Inv) + "";
		}
		return value;
	}

	private static SceneNode Bone(string avatarId, AvatarModel a, int i, Func<bool> alive)
	{
		string avatarId2 = avatarId;
		AvatarModel a2 = a;
		Func<bool> alive2 = alive;
		XsbSkeleton sk = a2.Rig.Skeleton;
		return new SceneNode
		{
			Id = avatarId2 + "/bone" + i.ToString(Inv),
			Name = BoneName(sk, i),
			Type = "Knochen " + i.ToString(Inv),
			Alive = alive2,
			CountChildren = () => ChildBones(sk, i).Count,
			Children = () => from c in ChildBones(sk, i)
				select Bone(avatarId2, a2, c, alive2),
			Inspect = delegate(InspectorBuilder b)
			{
				AvatarRig rig2 = a2.Rig;
				b.Section("Knochen");
				b.Info("Index", i.ToString(Inv));
				b.Info("Key", "0x" + ((i < sk.Key.Length) ? sk.Key[i] : 0).ToString("x16", Inv));
				int num = ((i < sk.Parent.Length) ? sk.Parent[i] : (-1));
				b.Info("Eltern", (num >= 0 && num < sk.Count) ? BoneName(sk, num) : "-");
				if (rig2 != null)
				{
					b.Section("Pose");
					b.Info("Position", rig2.LocalPosition(i));
					b.Info("Rotation", TransformMath.ToEuler(rig2.LocalRotation(i)), "0.##");
					b.Info("Scale", rig2.LocalScale(i));
					b.Info("Welt-Position", ToGame(Vector3.Transform(rig2.BonePosition(i), a2.EditorLastModel)));
					b.Section("Bind-Pose", collapsed: true);
					if (i < sk.Pos.Length)
					{
						b.Info("Position", sk.Pos[i]);
					}
					if (i < sk.Rot.Length)
					{
						b.Info("Rotation", TransformMath.ToEuler(sk.Rot[i]), "0.##");
					}
					if (i < sk.Scale.Length)
					{
						b.Info("Scale", sk.Scale[i]);
					}
				}
			},
			Gizmo = delegate(SceneGizmo g)
			{
				AvatarRig rig = a2.Rig;
				if (rig != null)
				{
					g.AxesRender(rig.BoneMatrix(i) * a2.EditorLastModel, 0.12f);
				}
			}
		};
	}

	private static void InspectAvatar(InspectorBuilder b, AvatarModel a)
	{
		AvatarConfig config = a.Config;
		b.Section("Avatar");
		b.Info("Bereit", a.Ready ? "yes" : "loading …");
		b.Info("Geschlecht", (config.Sex == 0) ? "Mann" : "Frau");
		b.Info("Herkunft", config.Ethnic switch
		{
			0 => "okzidental", 
			1 => "asiatisch", 
			2 => "afrikanisch", 
			_ => config.Ethnic.ToString(Inv), 
		});
		b.Info("Kleidung", (config.Items.Count == 0) ? "-" : string.Join(",", config.Items.Select((int x) => x.ToString(Inv))));
		b.Info("Farben", (config.Colors.Count == 0) ? "-" : string.Join(",", config.Colors.Select((int x) => x.ToString(Inv))));
		b.Info("Visible", a.Visible ? "yes" : "no");
		b.Info("In car / interior", (a.InCar ? "yes" : "no") + " / " + (a.Indoor ? "yes" : "no"));
		b.Info("Zuletzt gezeichnet", (a.EditorDrawnTick == 0L) ? "nie" : ((Environment.TickCount64 - a.EditorDrawnTick).ToString(Inv) + " ms her"));
		b.Info("Position", ToGame(a.EditorLastModel.Translation));
		b.Info("Knochen", a.Rig?.Skeleton.Count.ToString(Inv) ?? "-");
		List<string> list = a.EditorParts();
		b.Section("Parts");
		for (int i = 0; i < list.Count; i++)
		{
			b.Info("Teil " + i, list[i]);
		}
		List<AvatarModel.EditorPiece> list2 = a.EditorPieces();
		b.Section("Skinned Mesh Renderer");
		for (int j = 0; j < list2.Count; j++)
		{
			AvatarModel.EditorPiece editorPiece = list2[j];
			b.Info("Piece " + j, Nm(editorPiece.Name, "?") + ", " + editorPiece.Vertices.ToString("N0", Inv) + " Vertices, " + editorPiece.Triangles.ToString("N0", Inv) + " Dreiecke" + (editorPiece.Head ? ", Kopf" : ""));
		}
		HashSet<IGpuMaterial> hashSet = new HashSet<IGpuMaterial>(ReferenceEqualityComparer.Instance);
		foreach (AvatarModel.EditorPiece item in list2)
		{
			if (hashSet.Add(item.Material))
			{
				string text = Nm(item.Name, item.Material.Kind.ToString());
				b.Section("Material: " + text);
				b.Info("Shader", item.Material.Kind.ToString());
				TextureRows(b, item.Desc);
				MaterialDesc desc = item.Desc;
				IGpuMaterial gm = item.Material;
				string matId = RegisterMaterial(gm, desc, delegate
				{
					MaterialFactory.Renderer?.UpdateMaterial(gm, desc);
				});
				DescSection(b, text, desc, matId);
			}
		}
	}

	private static void Bounds(InspectorBuilder b, Vector3 min, Vector3 max)
	{
		if (min.X <= max.X)
		{
			b.Section("Bounds");
			b.Info("Center", (min + max) * 0.5f);
			b.Info("Size", max - min);
			b.Info("Min", min);
			b.Info("Max", max);
		}
	}

	private static void Parts(InspectorBuilder b, RenderPart[] parts)
	{
		for (int i = 0; i < parts.Length; i++)
		{
			RenderPart renderPart = parts[i];
			b.Info("Teil " + i, Tris(renderPart.Mesh) + ", " + MatName(renderPart.Material) + ((renderPart.Collision != null) ? (", Kollision " + renderPart.Collision.Length / 3 + " Dr.") : ""));
		}
	}

	private static string Tris(IGpuMesh? m)
	{
		if (m != null)
		{
			return (m.IndexCount / 3).ToString("N0", Inv) + " Dreiecke";
		}
		return "-";
	}

	private static string TrisSum(RenderPart[] parts)
	{
		return (((IEnumerable<RenderPart>)parts).Sum((Func<RenderPart, long>)((RenderPart p) => p.Mesh?.IndexCount ?? 0)) / 3).ToString("N0", Inv) + " Dreiecke";
	}

	private static string MatName(IGpuMaterial? m)
	{
		if (m == null)
		{
			return "";
		}
		if (MaterialFactory.TryGetInfo(m, out MaterialFactory.MaterialInfo info))
		{
			return Nm(info.Def.Name, info.Desc.Name ?? m.Kind.ToString());
		}
		return m.Kind.ToString();
	}

	public static string Tex(IGpuTexture? t)
	{
		if (t != null)
		{
			return Nm(t.Name, "") + "" + t.Width + "×" + t.Height + "" + t.Format;
		}
		return "-";
	}

	public static void Materials(InspectorBuilder b, IEnumerable<IGpuMaterial?> mats)
	{
		HashSet<IGpuMaterial> hashSet = new HashSet<IGpuMaterial>(ReferenceEqualityComparer.Instance);
		int num = 0;
		foreach (IGpuMaterial mat in mats)
		{
			if (mat == null || !hashSet.Add(mat))
			{
				continue;
			}
			if (++num > 32)
			{
				b.Section("Weitere Materialien");
				b.Info("…", "mehr als 32 – ausgelassen");
				break;
			}
			string text = MatName(mat);
			b.Section("Material: " + text);
			b.Info("Shader", mat.Kind.ToString());
			if (MaterialFactory.TryGetInfo(mat, out MaterialFactory.MaterialInfo info))
			{
				b.Info("Original-Material", info.Def.Name);
				b.Info("Original-Shader", "0x" + info.Def.ShaderSet.ToString("x16", Inv));
				b.Info("Blend", info.Blend.ToString());
				b.Info("Flags", Convert.ToHexString(info.Def.Flags));
				b.Info("Diffuse", V4(info.Def.Diffuse));
				b.Info("COLOR", (info.MainTexture ?? "-") + (info.Lightmap ? "" : ""));
				b.Info("DETAIL", (info.DetailTexture ?? "-") + (info.Packed ? "" : ""));
				b.Info("SHADOW", info.ShadowTexture ?? "-");
				b.Info("TINT", info.TintTexture ?? "-");
				b.Info("Road / ground / sprite", (info.Def.IsRoad ? "yes" : "no") + " / " + (info.Def.IsGround ? "yes" : "no") + " / " + (info.Sprite ? "yes" : "no"));
				if (info.Def.IsPoolWater)
				{
					b.Info("Pool-Wasser", "yes");
				}
				TextureRows(b, info.Desc);
				MaterialDesc desc = info.Desc;
				IGpuMaterial gm = mat;
				string matId = RegisterMaterial(mat, desc, delegate
				{
					MaterialFactory.Renderer?.UpdateMaterial(gm, desc);
				});
				DescSection(b, text, desc, matId);
			}
			else
			{
				b.Info("Details", "no material info");
			}
		}
	}

	private static void TextureRows(InspectorBuilder b, MaterialDesc d)
	{
		FieldInfo[] descFields = DescFields;
		foreach (FieldInfo fieldInfo in descFields)
		{
			if (typeof(IGpuTexture).IsAssignableFrom(fieldInfo.FieldType) && fieldInfo.GetValue(d) is IGpuTexture t)
			{
				b.Info("Texture " + fieldInfo.Name, Tex(t));
			}
		}
	}

	private static void DescSection(InspectorBuilder b, string name, MaterialDesc d, string? matId)
	{
		b.Section("Shader-Parameter: " + name, collapsed: true);
		if (matId != null)
		{
			b.Info("Note", "Changes apply to all objects with this material; name/type/textures are read-only");
		}
		FieldInfo[] descFields = DescFields;
		foreach (FieldInfo fieldInfo in descFields)
		{
			object value;
			try
			{
				value = fieldInfo.GetValue(d);
			}
			catch (Exception)
			{
				continue;
			}
			string text = ((matId != null && Editable(fieldInfo.FieldType)) ? ("mat:" + matId + ":" + fieldInfo.Name) : null);
			if (!(value is bool value2))
			{
				if (value is Vector3 v)
				{
					b.Vec3(fieldInfo.Name, v, text);
				}
				else if (text != null)
				{
					b.Text(fieldInfo.Name, EditText(value), text);
				}
				else
				{
					b.Info(fieldInfo.Name, Fmt(value));
				}
			}
			else
			{
				b.Bool(fieldInfo.Name, value2, text);
			}
		}
	}

	private static bool Editable(Type t)
	{
		if (!(t == typeof(float)) && !(t == typeof(int)) && !(t == typeof(bool)) && !(t == typeof(Vector2)) && !(t == typeof(Vector3)))
		{
			return t == typeof(Vector4);
		}
		return true;
	}

	private static string EditText(object? v)
	{
		if (!(v is float num))
		{
			if (!(v is int num2))
			{
				if (!(v is Vector2 vector))
				{
					if (v is Vector4 vector2)
					{
						return vector2.X.ToString("0.####", Inv) + "; " + vector2.Y.ToString("0.####", Inv) + "; " + vector2.Z.ToString("0.####", Inv) + "; " + vector2.W.ToString("0.####", Inv);
					}
					return Fmt(v);
				}
				return vector.X.ToString("0.####", Inv) + "; " + vector.Y.ToString("0.####", Inv);
			}
			return num2.ToString(Inv);
		}
		return num.ToString("0.#####", Inv);
	}

	internal static void BeginInspect()
	{
		_materials.Clear();
	}

	private static string RegisterMaterial(object owner, MaterialDesc desc, Action apply)
	{
		string text = EditorIds.Of(owner);
		_materials[text] = (desc, apply);
		return text;
	}

	internal static bool EditMaterial(string key, string value, out string message)
	{
		message = "";
		int num = key.LastIndexOf(':');
		if (!key.StartsWith("mat:", StringComparison.Ordinal) || num <= 4)
		{
			return false;
		}
		string key2 = key.Substring(4, num - 4);
		string field = key.Substring(num + 1);
		if (!_materials.TryGetValue(key2, out (MaterialDesc, Action) value2))
		{
			message = "Material no longer selected";
			return false;
		}
		FieldInfo fieldInfo = Array.Find(DescFields, (FieldInfo x) => x.Name == field);
		if (fieldInfo == null || !Editable(fieldInfo.FieldType))
		{
			message = field + " cannot be changed";
			return false;
		}
		object obj = Parse(fieldInfo.FieldType, value);
		if (obj == null)
		{
			message = field + ": invalid value \"" + value + "\"";
			return false;
		}
		fieldInfo.SetValue(value2.Item1, obj);
		try
		{
			value2.Item2();
		}
		catch (Exception ex)
		{
			message = field + ": " + ex.Message;
			return false;
		}
		message = field + " = " + value;
		return true;
	}

	private static object? Parse(Type t, string s)
	{
		s = (s ?? "").Trim();
		string[] p = (from x in s.Split(new char[2] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
			select x.Replace(',', '.')).ToArray();
		if (t == typeof(bool))
		{
			return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
		}
		if (t == typeof(int))
		{
			if (!int.TryParse(s, NumberStyles.Integer, Inv, out var result))
			{
				return null;
			}
			return result;
		}
		if (t == typeof(float))
		{
			if (p.Length != 1)
			{
				return null;
			}
			float num = F(0);
			if (!float.IsNaN(num))
			{
				return num;
			}
			return null;
		}
		if (t == typeof(Vector2))
		{
			if (p.Length != 2)
			{
				return null;
			}
			Vector2 vector = new Vector2(F(0), F(1));
			if (!float.IsNaN(vector.X + vector.Y))
			{
				return vector;
			}
			return null;
		}
		if (t == typeof(Vector3))
		{
			if (p.Length != 3)
			{
				return null;
			}
			Vector3 vector2 = new Vector3(F(0), F(1), F(2));
			if (!float.IsNaN(vector2.X + vector2.Y + vector2.Z))
			{
				return vector2;
			}
			return null;
		}
		if (t == typeof(Vector4))
		{
			if (p.Length != 4)
			{
				return null;
			}
			Vector4 vector3 = new Vector4(F(0), F(1), F(2), F(3));
			if (!float.IsNaN(vector3.X + vector3.Y + vector3.Z + vector3.W))
			{
				return vector3;
			}
			return null;
		}
		return null;
		float F(int i)
		{
			if (!float.TryParse(p[i], NumberStyles.Float, Inv, out var result2) || !float.IsFinite(result2))
			{
				return float.NaN;
			}
			return result2;
		}
	}

	private static string Fmt(object? v)
	{
		if (v != null)
		{
			if (!(v is IGpuTexture t))
			{
				if (!(v is IGpuMaterial m))
				{
					if (!(v is float num))
					{
						if (!(v is double num2))
						{
							if (!(v is bool))
							{
								if (!(v is Vector2 vector))
								{
									if (!(v is Vector3 v2))
									{
										if (!(v is Vector4 v3))
										{
											if (v is IFormattable formattable)
											{
												return formattable.ToString(null, Inv);
											}
											return v.ToString() ?? "";
										}
										return V4(v3);
									}
									return InspectorBuilder.V3(v2);
								}
								return vector.X.ToString("0.###", Inv) + "" + vector.Y.ToString("0.###", Inv);
							}
							return ((bool)v) ? "true" : "false";
						}
						return num2.ToString("0.####", Inv);
					}
					return num.ToString("0.####", Inv);
				}
				return MatName(m);
			}
			return Tex(t);
		}
		return "-";
	}

	private static string V4(Vector4 v)
	{
		return v.X.ToString("0.###", Inv) + "" + v.Y.ToString("0.###", Inv) + "" + v.Z.ToString("0.###", Inv) + "" + v.W.ToString("0.###", Inv);
	}
}

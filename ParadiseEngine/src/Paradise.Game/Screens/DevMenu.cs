using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using EdenSavegame;
using Paradise.Core.Audio;
using Paradise.Core.Avatars;
using Paradise.Core.Config;
using Paradise.Core.Diagnostics;
using Paradise.Core.Engine;
using Paradise.Core.Files;
using Paradise.Core.Flash;
using Paradise.Core.Formats;
using Paradise.Core.Net;
using Paradise.Core.Online;
using Paradise.Core.Online.Net;
using Paradise.Core.Physics;
using Paradise.Core.Platform;
using Paradise.Core.Racing;
using Paradise.Core.Render;
using Paradise.Core.Render.Original;
using Paradise.Core.Savegame;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Editor;
using Paradise.FlashDebug;
using Paradise.Game.Config;
using Paradise.Game.Gamemodes;
using Paradise.Game.Gameplay;
using Paradise.Game.Input;
using Paradise.Game.Online;
using Paradise.Game.Racing;
using Paradise.Game.World;
using Paradise.ProfilerUI;

namespace Paradise.Game.Screens;

public sealed class DevMenu : GameModule
{
	private struct StaticDraw
	{
		public StaticCollider Collider;

		public Vector3 Center;

		public Vector3 Half;

		public float DistSq;

		public int From;

		public int Count;

		public int ColorIndex;

		public bool Flat;

		public string Label;
	}

	private sealed class Item
	{
		public string Label = "";

		public Func<string>? Value;

		public Action<int>? Change;

		public Action? Reset;

		public Action? Activate;

		public Func<List<Item>>? Sub;

		public bool Header;

		public bool Placeholder;
	}

	private sealed class Page
	{
		public string Title = "";

		public Func<List<Item>> Build = () => new List<Item>();

		public int Sel;

		public int Top;
	}

	private bool _dbgRays;

	private bool _dbgForces;

	private bool _dbgVelocity;

	private bool _dbgWheelLabels;

	private static readonly System.Numerics.Vector4 LGreen = new System.Numerics.Vector4(0.3f, 1f, 0.3f, 1f);

	private static readonly System.Numerics.Vector4 LRed = new System.Numerics.Vector4(1f, 0.25f, 0.2f, 1f);

	private static readonly System.Numerics.Vector4 LCyan = new System.Numerics.Vector4(0.3f, 0.95f, 1f, 1f);

	private static readonly System.Numerics.Vector4 LYellow = new System.Numerics.Vector4(1f, 0.92f, 0.2f, 1f);

	private static readonly System.Numerics.Vector4 LOrange = new System.Numerics.Vector4(1f, 0.6f, 0.1f, 1f);

	private static readonly System.Numerics.Vector4 LMagenta = new System.Numerics.Vector4(1f, 0.3f, 1f, 1f);

	private static readonly System.Numerics.Vector4 LWhite = new System.Numerics.Vector4(1f, 1f, 1f, 1f);

	private bool _collisionBoxes;

	private float _collisionRange = 100f;

	private float _detailDistance = 45f;

	private bool _boxRoad = true;

	private bool _boxObjects = true;

	private bool _boxProps = true;

	private bool _boxFixed = true;

	private bool _boxLoose = true;

	private bool _boxCars = true;

	private bool _boxLabels = true;

	private bool _aiPaths;

	private int _maxCollisionLabels = 48;

	private int _detailTriBudget = 60000;

	private int _pathLineBudget = 1500;

	private int _labelsThisFrame;

	private int _colPreset;

	private int _colRoad;

	private int _colObjects = 8;

	private int _colProps = 5;

	private int _colFixed = 1;

	private int _colLoose = 2;

	private int _colCar = 3;

	private int _colPath = 10;

	private int _cntRoad;

	private int _cntObjects;

	private int _cntProps;

	private int _cntFixed;

	private int _cntLoose;

	private int _cntCar;

	private int _cntPath;

	private static readonly string[] CollisionColorNames = new string[12]
	{
		"Orange", "Blue", "Green", "Cyan", "Yellow", "Red", "Magenta", "White", "Lime", "Purple",
		"Pink", "Steel"
	};

	private static readonly System.Numerics.Vector4[] CollisionColors = new System.Numerics.Vector4[12]
	{
		new System.Numerics.Vector4(1f, 0.55f, 0.12f, 1f),
		new System.Numerics.Vector4(0.3f, 0.6f, 1f, 1f),
		new System.Numerics.Vector4(0.25f, 1f, 0.45f, 1f),
		new System.Numerics.Vector4(0.2f, 1f, 1f, 1f),
		new System.Numerics.Vector4(1f, 0.92f, 0.2f, 1f),
		new System.Numerics.Vector4(1f, 0.25f, 0.2f, 1f),
		new System.Numerics.Vector4(1f, 0.35f, 0.9f, 1f),
		new System.Numerics.Vector4(0.95f, 0.95f, 0.95f, 1f),
		new System.Numerics.Vector4(0.6f, 1f, 0.2f, 1f),
		new System.Numerics.Vector4(0.65f, 0.4f, 1f, 1f),
		new System.Numerics.Vector4(1f, 0.5f, 0.7f, 1f),
		new System.Numerics.Vector4(0.5f, 0.7f, 0.8f, 1f)
	};

	private static readonly string[] CollisionPresetNames = new string[5] { "Default", "Neon", "Colorblind", "Warm", "Cool" };

	private static readonly int[][] CollisionPresets = new int[5][]
	{
		new int[7] { 0, 8, 5, 1, 2, 3, 10 },
		new int[7] { 4, 7, 8, 6, 9, 3, 10 },
		new int[7] { 0, 4, 5, 1, 7, 6, 10 },
		new int[7] { 5, 0, 8, 10, 1, 2, 3 },
		new int[7] { 9, 1, 3, 7, 4, 8, 10 }
	};

	private const int VK_F1 = 112;

	private const int VK_UP = 38;

	private const int VK_DOWN = 40;

	private const int VK_LEFT = 37;

	private const int VK_RIGHT = 39;

	private const int VK_PGUP = 33;

	private const int VK_PGDN = 34;

	private const int VK_HOME = 36;

	private const int VK_END = 35;

	private const int VK_DELETE = 46;

	private const int VK_ENTER = 13;

	private const int VK_BACK = 8;

	private const int VK_SHIFT = 16;

	private const int VK_LSHIFT = 160;

	private const int VK_RSHIFT = 161;

	private const int VK_CONTROL = 17;

	private const int VK_LCONTROL = 162;

	private const int VK_RCONTROL = 163;

	private static readonly uint CText = ObjectVertex.PackColor(140, 150, 158, byte.MaxValue);

	private static readonly uint CTextDim = ObjectVertex.PackColor(70, 78, 86, byte.MaxValue);

	private static readonly uint CValue = ObjectVertex.PackColor(112, 122, 130, byte.MaxValue);

	private static readonly uint CHeader = ObjectVertex.PackColor(88, 98, 106, byte.MaxValue);

	private static readonly uint CPath = ObjectVertex.PackColor(100, 110, 118, byte.MaxValue);

	private static readonly uint CAccent = ObjectVertex.PackColor(70, 120, 145, byte.MaxValue);

	private static readonly uint CWarn = ObjectVertex.PackColor(150, 95, 35, byte.MaxValue);

	private static readonly uint CBack = ObjectVertex.PackColor(0, 0, 0, byte.MaxValue);

	private static readonly uint CPanel = ObjectVertex.PackColor(10, 12, 14, byte.MaxValue);

	private static readonly uint CSelect = ObjectVertex.PackColor(16, 24, 30, byte.MaxValue);

	private static readonly uint CLine = ObjectVertex.PackColor(30, 36, 42, byte.MaxValue);

	// Unity TDU2InputOverlay: Farben der Balken und des Hintergrunds.
	private static readonly uint COverlayBack = ObjectVertex.PackColor(0, 0, 0, 184);
	private static readonly uint CDevice = ObjectVertex.PackColor(255, 217, 77, byte.MaxValue);
	private static readonly uint CBarBack = ObjectVertex.PackColor(255, 255, 255, 31);
	private static readonly uint CBarSteer = ObjectVertex.PackColor(102, 204, 255, byte.MaxValue);
	private static readonly uint CBarGas = ObjectVertex.PackColor(89, 230, 77, byte.MaxValue);
	private static readonly uint CBarBrake = ObjectVertex.PackColor(255, 77, 64, byte.MaxValue);
	private static readonly uint CBarClutch = ObjectVertex.PackColor(255, 179, 51, byte.MaxValue);
	private static readonly uint CBarAxis = ObjectVertex.PackColor(230, 230, 230, byte.MaxValue);
	private static readonly uint CBarDim = ObjectVertex.PackColor(128, 128, 128, byte.MaxValue);
	private static readonly uint CCenterTick = ObjectVertex.PackColor(255, 255, 255, 153);
	private static readonly uint CBarVib = ObjectVertex.PackColor(204, 128, 255, byte.MaxValue);

	private readonly IRenderer _r;

	private readonly IGameWindow _window;

	private readonly TerrainModule _terrain;

	private readonly VehicleModule _vehicles;

	private readonly LaunchOptions _opts;

	private readonly GpuMonitor _gpu;

	private double _nextGpuSample;

	private readonly List<Page> _stack = new List<Page>();

	private List<Item> _items = new List<Item>();

	private bool _open;

	private bool _blockedByMe;

	private float _panelNX;

	private float _panelNY;

	private bool _dragging;

	private float _panelW;

	private float _panelH;

	private float _panelHeaderH;

	private float _panelRx;

	private float _panelRy;

	private float _slideX;

	private float _slideY;

	private bool _drive;

	private double _time;

	private double _repeatAt;

	private int _repeatKey;

	private string? _toast;

	private double _toastUntil;

	private readonly List<ObjectVertex> _v = new List<ObjectVertex>(8192);

	private readonly List<uint> _i = new List<uint>(12288);

	private FontAtlas? _font;

	private bool _fontFailed;

	private readonly Dictionary<string, object> _ph = new Dictionary<string, object>();

	private float _fps;

	private float _ftAvg;

	private float _ftMin;

	private float _ftMax;

	private int _sampleFrames;

	private float _sampleTime;

	private float _sampleMin = float.MaxValue;

	private float _sampleMax;

	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

	private static int _discoveryRoadIndex;

	private static int _discoveryAddPoints = 100;

	private static readonly (string island, string name, string arrival)[] Islands = new(string, string, string)[3]
	{
		("ibiza", "Ibiza", "EAIR"),
		("hawai", "Hawaii / Oahu", "EAIR"),
		("casino", "Casino", "ELAND")
	};

	private static readonly string[] SexNames = new string[2] { "Male", "Female" };

	private static readonly string[] EthNames = new string[3] { "European", "Asian", "African" };

	private static readonly (ClothePart part, string name)[] AvatarParts = new(ClothePart, string)[11]
	{
		(ClothePart.Hair, "Hair"),
		(ClothePart.Top, "Top"),
		(ClothePart.Bottom, "Trousers / skirt"),
		(ClothePart.Dress, "Dress"),
		(ClothePart.Shoes, "Shoes"),
		(ClothePart.Helmet, "Hat / helmet"),
		(ClothePart.Glasses, "Glasses"),
		(ClothePart.Gloves, "Gloves"),
		(ClothePart.Beard, "Beard"),
		(ClothePart.Accessories, "Accessories"),
		(ClothePart.Belt, "Belt")
	};

	private List<string>? _clipNames;

	private readonly List<VmfPlayer> _flashViewers = new List<VmfPlayer>();

	private bool _flashLogAuto;

	private double _flashLogNext;

	private bool _editorRoots;

	private const int SgPageSize = 25;

	private const int SgMaxDepth = 6;

	private JsonDocument? _sgLiveDoc;

	private JsonDocument? _sgFileDoc;

	private XObject? _sgXmbfRoot;

	private string _sgViewInfo = "no data loaded";

	private static readonly JsonSerializerOptions SgPrettyJson = new JsonSerializerOptions
	{
		IncludeFields = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		NumberHandling = (JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals)
	};

	private static readonly string[] AidNames = new string[4] { "Secure", "Sport", "Hypersport", "Off" };

	private static readonly string[] KindNames = new string[7] { "Automatic", "Sun", "Clouds", "Rain", "Before rain", "After rain", "Thunderstorm" };

	private static readonly string[] MsaaNames = new string[4] { "Off", "2x", "4x", "8x" };

	private static readonly int[] MsaaValues = new int[4] { 1, 2, 4, 8 };

	private static readonly string[] UpscalerNames = new string[3] { "Off", "FSR 1", "FSR 3" };

	private static readonly int[] UpscalerValues = new int[3] { 0, 1, 3 }; // 0 = off, 1 = FSR 1, 3 = FSR 3 (wie FidelityFX-Upscaler-IDs)

	private static readonly string[] UpscalerQualityNames = new string[4] { "Quality", "Balanced", "Performance", "Ultra Performance" };

	private static readonly string[] TexNames = new string[4] { "Full", "1/2", "1/4", "1/8" };

	private static readonly string[] ShadowResNames = new string[4] { "512", "1024", "2048", "4096" };

	private static readonly int[] ShadowResValues = new int[4] { 512, 1024, 2048, 4096 };

	private static readonly string[] ShadowCascadeNames = new string[7] { "2", "3", "4", "5", "6", "7", "8" };

	private static readonly int[] ShadowCascadeValues = new int[7] { 2, 3, 4, 5, 6, 7, 8 };

	private double _raceCatalogRefreshAt;

	private static readonly string[] StageNames3 = new string[3] { "Sky stage", "Here", "At the bank's spot" };

	private List<string>? _pnjIds;

	private bool AnyCarDebug
	{
		get
		{
			if (!_dbgRays && !_dbgForces && !_dbgVelocity)
			{
				return _dbgWheelLabels;
			}
			return true;
		}
	}

	private Traffic? Traffic => _vehicles.Traffic;

	public bool IsOpen => _open;

	/// <summary>Unity TDU2InputOverlay: Achsen/Pedale/Knöpfe/Vibration als Balken, solange das DevMenü zu ist.</summary>
	public static bool InputOverlayVisible;

	private static AvatarConfig Cfg => AvatarConfigs.Local;

	private static string SgNick
	{
		get
		{
			if (!string.IsNullOrEmpty(TDU2Session.Nick))
			{
				return TDU2Session.Nick;
			}
			return "_offline";
		}
	}

	private static string SgDebugDir => Path.Combine(UserPaths.Saves, "Debug");

	private static string SgJsonPath => SgFile("json");

	private static string SgXmbfPath => SgFile("xmbf");

	private static string SgBuiltXmbfPath => SgFile("xmbf", ".live");

	private static string SgDataBackupPath => SgFile("DATA", ".backup");

	private MapStreamer? Streamer => _terrain.Streamer;

	private VehicleManager? Mgr => _vehicles.Manager;

	private WorldWeather? Weather => _terrain.Weather;

	private static RaceDebugView RaceView => RaceDebugView.Instance;

	private CutsceneTester Tester => CutsceneTester.Get(_r, _terrain, _vehicles);

	private AnimViewer Viewer => AnimViewer.Get(_r, _terrain, _vehicles);

	private void SetAllCarDebug(bool on)
	{
		_dbgRays = (_dbgForces = (_dbgVelocity = (_dbgWheelLabels = on)));
	}

	private List<Item> CarPhysicsMenu()
	{
		VehicleManager i = Mgr;
		List<Item> list = new List<Item> { Head("TIRES") };
		if (i == null)
		{
			list.Add(Info("no vehicle manager"));
			return list;
		}
		list.Add(Float("Grip scale", () => i.Current?.GripScale ?? i.GripScale, delegate(float v)
		{
			i.GripScale = v;
			if (i.Current != null)
			{
				i.Current.GripScale = v;
			}
		}, 0.02f, 0.3f, 1.2f, new VehicleConfig().GripScale, "0.00"));
		list.Add(Float("Grip on dirt", () => i.Current?.DirtGrip ?? 0.82f, delegate(float v)
		{
			if (i.Current != null)
			{
				i.Current.DirtGrip = v;
			}
		}, 0.02f, 0.2f, 1.5f, 0.82f, "0.00"));
		list.Add(Float("Grip on grass", () => i.Current?.GrassGrip ?? 0.72f, delegate(float v)
		{
			if (i.Current != null)
			{
				i.Current.GrassGrip = v;
			}
		}, 0.02f, 0.2f, 1.5f, 0.72f, "0.00"));
		list.Add(Float("Tire load max", () => Vehicle.TireLoadMax, delegate(float v)
		{
			Vehicle.TireLoadMax = v;
		}, 0.1f, 0f, 10f, new VehicleConfig().TireLoadMax, "0.0", " x"));
		list.Add(Float("Grip vs. rollover", () => Vehicle.GripVsRollover, delegate(float v)
		{
			Vehicle.GripVsRollover = v;
		}, 0.05f, 0f, 2f, new VehicleConfig().GripVsRollover, "0.00"));
		list.Add(Head("SUSPENSION"));
		list.Add(Float("Damper knee", () => Vehicle.DamperKnee, delegate(float v)
		{
			Vehicle.DamperKnee = v;
		}, 0.05f, 0.05f, 5f, new VehicleConfig().DamperKnee, "0.00", " m/s"));
		list.Add(Float("Damper ratio above knee", () => Vehicle.DamperHighRatio, delegate(float v)
		{
			Vehicle.DamperHighRatio = v;
		}, 0.05f, 0.05f, 1f, new VehicleConfig().DamperHighRatio, "0.00"));
		list.Add(Float("Wheel max rise", () => Vehicle.WheelMaxRise, delegate(float v)
		{
			Vehicle.WheelMaxRise = v;
		}, 0.25f, 0f, 20f, new VehicleConfig().WheelMaxRise, "0.00", " m/s"));
		list.Add(Float("Bump rebound", () => Vehicle.BumpRebound, delegate(float v)
		{
			Vehicle.BumpRebound = v;
		}, 0.05f, 0f, 1f, new VehicleConfig().BumpRebound, "0.00"));
		list.Add(Float("Min. rollover", () => Vehicle.MinRolloverG, delegate(float v)
		{
			Vehicle.MinRolloverG = v;
		}, 0.05f, 0f, 3f, new VehicleConfig().MinRolloverG, "0.00", " g"));
		list.Add(Head("DRIVETRAIN / UPGRADES"));
		list.Add(Float("Torque factor", () => i.Current?.UpgradeTorque ?? 1f, delegate(float v)
		{
			if (i.Current != null)
			{
				i.Current.UpgradeTorque = v;
			}
		}, 0.05f, 0.2f, 5f, 1f, "0.00", " x"));
		list.Add(Float("Top speed factor", () => i.Current?.UpgradeTopSpeed ?? 1f, delegate(float v)
		{
			if (i.Current != null)
			{
				i.Current.UpgradeTopSpeed = v;
			}
		}, 0.05f, 0.2f, 3f, 1f, "0.00", " x"));
		list.Add(Float("Braking factor", () => i.Current?.UpgradeBraking ?? 1f, delegate(float v)
		{
			if (i.Current != null)
			{
				i.Current.UpgradeBraking = v;
			}
		}, 0.05f, 0.2f, 3f, 1f, "0.00", " x"));
		list.Add(Bool("Auto hold", () => i.Current?.AutoHold ?? true, delegate(bool v)
		{
			if (i.Current != null)
			{
				i.Current.AutoHold = v;
			}
		}, def: true));
		list.Add(Float("Physics step", () => i.FixedTimeStep * 1000f, delegate(float v)
		{
			i.FixedTimeStep = v / 1000f;
		}, 1f, 5f, 20f, new VehicleConfig().FixedTimeStep * 1000f, "0", " ms"));
		list.Add(Head("BODY"));
		list.Add(Info("Mass", () => (i.Current == null) ? "-" : (Num(i.Current.Rigidbody.mass) + " kg")));
		list.Add(Info("Centre of mass", delegate
		{
			Vehicle current2 = i.Current;
			return (current2 == null) ? "-" : V3(current2.Rigidbody.centerOfMass);
		}));
		list.Add(Info("Inertia", delegate
		{
			Vehicle current = i.Current;
			return (current == null) ? "-" : V3(current.Rigidbody.inertiaTensor, "0");
		}));
		list.Add(Info("Physics config", () => i.Current?.Config?.Source ?? "-"));
		list.Add(Head(""));
		list.Add(Info("Defaults: Game/Config/VehicleConfig.cs"));
		return list;
	}

	private List<Item> CarDebugMenu()
	{
		VehicleManager i = Mgr;
		List<Item> list = new List<Item> { Head("DEBUG LINES IN THE WORLD") };
		list.Add(Bool("Suspension rays", () => _dbgRays, delegate(bool v)
		{
			_dbgRays = v;
		}, def: false));
		list.Add(Bool("Tire forces", () => _dbgForces, delegate(bool v)
		{
			_dbgForces = v;
		}, def: false));
		list.Add(Bool("Velocity / forward arrows", () => _dbgVelocity, delegate(bool v)
		{
			_dbgVelocity = v;
		}, def: false));
		list.Add(Bool("Wheel labels", () => _dbgWheelLabels, delegate(bool v)
		{
			_dbgWheelLabels = v;
		}, def: false));
		if (i == null)
		{
			list.Add(Info("no vehicle manager"));
			return list;
		}
		list.Add(Head("ENGINE / GEARBOX"));
		list.Add(Info("Speed / forward", delegate
		{
			Vehicle current7 = i.Current;
			return (current7 == null) ? "-" : (Num(current7.SpeedKmh) + " km/h / " + Num(current7.ForwardSpeed * 3.6f) + " km/h");
		}));
		list.Add(Info("Rpm / gear", delegate
		{
			Vehicle current6 = i.Current;
			return (current6 == null) ? "-" : (Num(current6.Rpm) + " / " + ((current6.Gear < 0) ? "R" : ((current6.Gear == 0) ? "N" : current6.Gear.ToString(Inv))) + (current6.Shifting ? "" : ""));
		}));
		list.Add(Info("Throttle / brake / hold", delegate
		{
			Vehicle current5 = i.Current;
			return (current5 == null) ? "-" : (current5.Throttle.ToString("0.00", Inv) + " / " + current5.Brake.ToString("0.00", Inv) + " / " + current5.HoldAmount.ToString("0.00", Inv));
		}));
		list.Add(Info("Engine torque", delegate
		{
			Vehicle current4 = i.Current;
			return (current4 == null) ? "-" : (Num(current4.EngineTorque) + " Nm");
		}));
		list.Add(Info("Steering angle", delegate
		{
			Vehicle current3 = i.Current;
			return (current3 == null) ? "-" : (current3.SteeringAngle.ToString("+0.0;-0.0", Inv) + "°");
		}));
		list.Add(Info("Angular velocity", delegate
		{
			Vehicle current2 = i.Current;
			return (current2 == null) ? "-" : V3(current2.Rigidbody.angularVelocity);
		}));
		Vehicle current = i.Current;
		if (current != null)
		{
			list.Add(Head("WHEELS"));
			Vehicle.Wheel[] wheels = current.Wheels;
			foreach (Vehicle.Wheel wheel in wheels)
			{
				Vehicle.Wheel w = wheel;
				list.Add(Info(w.Name + (w.Front ? " F" : " R") + (w.Left ? "L" : "R") + (w.Driven ? " *" : ""), () => (w.Grounded ? "" : "AIR") + w.Compression.ToString("0.000", Inv) + "" + Num(w.Fz) + " N " + w.SlipRatio.ToString("+0.00;-0.00", Inv) + "" + (w.SlipAngle * (180f / (float)Math.PI)).ToString("+0.0;-0.0", Inv) + "° s" + w.Surface));
			}
		}
		list.Add(Head(""));
		list.Add(Info("Slip angle shown in degrees"));
		return list;
	}

	private static string V3(Vector3 v, string fmt = "0.00")
	{
		return v.X.ToString(fmt, Inv) + " / " + v.Y.ToString(fmt, Inv) + " / " + v.Z.ToString(fmt, Inv);
	}

	private void CollectCarDebug()
	{
		if (!_dbgRays && !_dbgForces && !_dbgVelocity && !_dbgWheelLabels)
		{
			return;
		}
		VehicleManager mgr = Mgr;
		Vehicle vehicle = mgr?.Current;
		if (mgr == null || vehicle == null || _terrain.MapView)
		{
			return;
		}
		VehiclePose transform = vehicle.transform;
		Vector3 up = transform.up;
		Vehicle.Wheel[] wheels = vehicle.Wheels;
		foreach (Vehicle.Wheel wheel in wheels)
		{
			Vector3 vector = transform.TransformPoint(wheel.Anchor);
			Vector3 vector2 = vector + up * wheel.TravelUp;
			Vector3 vector3 = vector2 - up * (wheel.TravelUp + wheel.Droop + wheel.Radius);
			Vector3 unityLocal = (wheel.Grounded ? wheel.ContactPoint : vector3);
			if (_dbgRays)
			{
				DebugLines.Line(mgr.ToGame(vector2), mgr.ToGame(unityLocal), wheel.Grounded ? LGreen : LRed);
				if (wheel.Grounded)
				{
					DebugLines.Ray(mgr.ToGame(wheel.ContactPoint), U.Mirror(wheel.ContactNormal), 0.5f, LWhite);
				}
			}
			if (_dbgForces && wheel.Grounded)
			{
				Vector3 contactNormal = wheel.ContactNormal;
				Vector3 v = U.ProjectOnPlane(U.Rot(U.AngleAxis(wheel.SteerAngle, up), transform.forward), contactNormal);
				v = U.Normalized(v);
				Vector3 vector4 = Vector3.Cross(contactNormal, v);
				float num = 1f / MathF.Max(1000f, wheel.StaticLoad);
				Vector3 from = mgr.ToGame(wheel.ContactPoint + contactNormal * 0.05f);
				DebugLines.Ray(from, U.Mirror(contactNormal), wheel.Fz * num, LYellow);
				if (MathF.Abs(wheel.Fx) > 1f)
				{
					DebugLines.Ray(from, U.Mirror(v * MathF.Sign(wheel.Fx)), MathF.Abs(wheel.Fx) * num, LOrange);
				}
				if (MathF.Abs(wheel.Fy) > 1f)
				{
					DebugLines.Ray(from, U.Mirror(vector4 * MathF.Sign(wheel.Fy)), MathF.Abs(wheel.Fy) * num, LCyan);
				}
			}
			if (_dbgWheelLabels)
			{
				DebugLines.Label(mgr.ToGame(vector + up * 0.6f), wheel.Name + "\n" + Num(wheel.Fz) + " N\n" + wheel.SlipRatio.ToString("+0.00;-0.00", Inv) + " / " + (wheel.SlipAngle * (180f / (float)Math.PI)).ToString("+0.0;-0.0", Inv) + "°", wheel.Grounded ? LGreen : LRed);
			}
		}
		if (_dbgVelocity)
		{
			Vector3 vector5 = mgr.ToGame(vehicle.Rigidbody.worldCenterOfMass);
			Vector3 direction = U.Mirror(vehicle.Rigidbody.linearVelocity);
			float num2 = direction.Length();
			if (num2 > 0.2f)
			{
				DebugLines.Arrow(vector5, direction, MathF.Min(num2 * 0.25f, 15f), LMagenta, 0.8f);
			}
			DebugLines.Arrow(vector5 + Vector3.UnitY * 0.8f, U.Mirror(transform.forward), 3f, LWhite, 0.6f);
			DebugLines.Circle(vector5, 0.3f, LMagenta, 12);
		}
	}

	private List<Item> TrafficMenu()
	{
		Traffic tr = Traffic;
		List<Item> list = new List<Item> { Head("STATUS") };
		if (tr == null)
		{
			list.Add(Info("no traffic loaded"));
			return list;
		}
		list.Add(Info("Traffic cars", () => tr.CarCount + " / " + TrafficConfig.MaxCars));
		list.Add(Info("State", () => tr.Status.Replace('\n', ' ')));
		list.Add(Head("TRAFFIC"));
		list.Add(Bool("Traffic", () => TrafficConfig.Enabled, delegate(bool v)
		{
			TrafficConfig.Enabled = v;
		}, def: true));
		list.Add(Int("Max cars", () => TrafficConfig.MaxCars, delegate(int v)
		{
			TrafficConfig.MaxCars = v;
		}, 0, 40, 12));
		list.Add(Bool("Density by time of day", () => TrafficConfig.DensityByTime, delegate(bool v)
		{
			TrafficConfig.DensityByTime = v;
		}, def: false));
		list.Add(Float("Speed factor", () => TrafficConfig.SpeedFactor, delegate(float v)
		{
			TrafficConfig.SpeedFactor = v;
		}, 0.02f, 0.5f, 1.3f, 0.92f, "0.00"));
		list.Add(Float("Spawn distance min", () => TrafficConfig.SpawnMin, delegate(float v)
		{
			TrafficConfig.SpawnMin = MathF.Min(v, TrafficConfig.SpawnMax - 20f);
		}, 10f, 40f, 1000f, 180f, "0", " m"));
		list.Add(Float("Spawn distance max", () => TrafficConfig.SpawnMax, delegate(float v)
		{
			TrafficConfig.SpawnMax = MathF.Max(v, TrafficConfig.SpawnMin + 20f);
		}, 10f, 80f, 1500f, 400f, "0", " m"));
		list.Add(Float("Despawn beyond", () => TrafficConfig.DespawnDistance, delegate(float v)
		{
			TrafficConfig.DespawnDistance = MathF.Max(v, TrafficConfig.SpawnMax + 20f);
		}, 10f, 100f, 2000f, 520f, "0", " m"));
		list.Add(Action_("Remove all traffic cars", delegate
		{
			tr.ClearCars();
			Toast("Traffic cleared");
		}));
		list.Add(Head("DRIVING"));
		list.Add(Float("Max acceleration", () => TrafficConfig.AccelMax, delegate(float v)
		{
			TrafficConfig.AccelMax = v;
		}, 0.1f, 0.3f, 6f, 1.6f, "0.0", " m/s²"));
		list.Add(Float("Comfortable braking", () => TrafficConfig.ComfortBrake, delegate(float v)
		{
			TrafficConfig.ComfortBrake = v;
		}, 0.1f, 0.5f, 8f, 2.4f, "0.0", " m/s²"));
		list.Add(Float("Emergency braking", () => TrafficConfig.EmergencyBrake, delegate(float v)
		{
			TrafficConfig.EmergencyBrake = v;
		}, 0.5f, 2f, 15f, 8f, "0.0", " m/s²"));
		list.Add(Float("Minimum gap", () => TrafficConfig.MinGap, delegate(float v)
		{
			TrafficConfig.MinGap = v;
		}, 0.5f, 1f, 15f, 3.5f, "0.0", " m"));
		list.Add(Float("Time gap", () => TrafficConfig.TimeGap, delegate(float v)
		{
			TrafficConfig.TimeGap = v;
		}, 0.1f, 0.5f, 4f, 1.4f, "0.0", " s"));
		list.Add(Float("Jerk", () => TrafficConfig.Jerk, delegate(float v)
		{
			TrafficConfig.Jerk = v;
		}, 0.5f, 1f, 40f, 5f, "0.0", " m/s³"));
		list.Add(Float("Jerk", () => TrafficConfig.EmergencyJerk, delegate(float v)
		{
			TrafficConfig.EmergencyJerk = v;
		}, 1f, 5f, 100f, 30f, "0", " m/s³"));
		list.Add(Float("Yield only if stopping below", () => TrafficConfig.YieldBrake, delegate(float v)
		{
			TrafficConfig.YieldBrake = v;
		}, 0.1f, 1f, 10f, 3.5f, "0.0", " m/s²"));
		list.Add(Float("Player prediction", () => TrafficConfig.PlayerPredict, delegate(float v)
		{
			TrafficConfig.PlayerPredict = v;
		}, 0.1f, 0f, 4f, 1.5f, "0.0", " s"));
		list.Add(Head("PHYSICS & SOUND"));
		list.Add(Int("Cars with physics", () => TrafficConfig.PhysicsMax, delegate(int v)
		{
			TrafficConfig.PhysicsMax = v;
		}, 0, 30, 6));
		list.Add(Float("Physics radius", () => TrafficConfig.PhysicsRadius, delegate(float v)
		{
			TrafficConfig.PhysicsRadius = v;
		}, 10f, 20f, 500f, 100f, "0", " m"));
		list.Add(Float("Crash above", () => TrafficConfig.CrashSpeed, delegate(float v)
		{
			TrafficConfig.CrashSpeed = v;
		}, 0.5f, 0.5f, 20f, 3.5f, "0.0", " m/s"));
		list.Add(Bool("Traffic sounds", () => TrafficConfig.Sounds, delegate(bool v)
		{
			TrafficConfig.Sounds = v;
		}, def: true));
		list.Add(Int("Max sounds", () => TrafficConfig.SoundMax, delegate(int v)
		{
			TrafficConfig.SoundMax = v;
		}, 0, 16, 3));
		list.Add(Float("Sound radius", () => TrafficConfig.SoundRadius, delegate(float v)
		{
			TrafficConfig.SoundRadius = v;
		}, 5f, 10f, 300f, 70f, "0", " m"));
		list.Add(Head("DEBUG"));
		AddTrafficDebugToggles(list);
		AddAiPathToggles(list);
		list.Add(Info("Live cars / details / path colors", () => "Vehicle > Traffic cars · Debug > AI path"));
		return list;
	}

	private List<Item> TrafficCarListMenu()
	{
		Traffic traffic = Traffic;
		List<Item> list = new List<Item> { Head("TRAFFIC CARS") };
		if (traffic == null)
		{
			list.Add(Info("no traffic"));
			return list;
		}
		foreach (var item in traffic.Info())
		{
			(uint id, string model, string mode, float kmh, float dist, string lane) cc = item;
			list.Add(Info($"{cc.id & 0xFFFF:X4}{cc.model}", () => $"{cc.mode}{cc.kmh:0} km/h {cc.dist:0} m {cc.lane}"));
		}
		if (list.Count == 1)
		{
			list.Add(Info("no cars"));
		}
		return list;
	}

	private void AddTrafficDebugToggles(List<Item> l)
	{
		l.Add(Bool("Traffic debug", () => TrafficConfig.ShowDebug, delegate(bool v)
		{
			TrafficConfig.ShowDebug = v;
		}, def: false));
		l.Add(Bool("Debug: planned routes", () => TrafficConfig.DebugRoutes, delegate(bool v)
		{
			TrafficConfig.DebugRoutes = v;
		}, def: true));
		l.Add(Bool("Debug: forward arrows", () => TrafficConfig.DebugArrows, delegate(bool v)
		{
			TrafficConfig.DebugArrows = v;
		}, def: true));
		l.Add(Bool("Debug: speed/heading/target labels", () => TrafficConfig.DebugLabels, delegate(bool v)
		{
			TrafficConfig.DebugLabels = v;
		}, def: true));
		l.Add(Float("Debug range", () => TrafficConfig.DebugRange, delegate(float v)
		{
			TrafficConfig.DebugRange = v;
		}, 10f, 20f, 600f, 140f, "0", " m"));
	}

	private List<Item> TrafficDebugMenu()
	{
		Traffic tr = Traffic;
		List<Item> list = new List<Item> { Head("TRAFFIC DEBUG DETAILS") };
		if (tr == null)
		{
			list.Add(Info("no traffic"));
			return list;
		}
		AddTrafficDebugToggles(list);
		AddAiPathToggles(list);
		list.Add(Info("Cars / physics / crash / stopped", delegate
		{
			var (num2, num3, num4) = tr.Counts();
			return tr.CarCount + " / " + num2 + " / " + num3 + " / " + num4;
		}));
		list.Add(Info("Physics / Sound", () => "p:" + TrafficConfig.PhysicsMax + "@" + Num(TrafficConfig.PhysicsRadius) + "m s:" + TrafficConfig.SoundMax + "@" + Num(TrafficConfig.SoundRadius) + "m"));
		list.Add(Info("Road network", () => (tr.Lanes == null) ? "-" : "loaded"));
		list.Add(Head("NEARBY CARS"));
		int num = 0;
		foreach (var item in tr.Info())
		{
			if (num++ > 25)
			{
				break;
			}
			(uint id, string model, string mode, float kmh, float dist, string lane) cc = item;
			string distStr = ((cc.dist < 1000f) ? (cc.dist.ToString("0", Inv) + "m") : ((cc.dist / 1000f).ToString("0.0", Inv) + "km"));
			list.Add(Info(cc.model + " [" + cc.mode + "]", () => $"{cc.kmh:0} km/h {distStr} Lane:{cc.lane}"));
		}
		if (num == 0)
		{
			list.Add(Info("No traffic cars"));
		}
		return list;
	}

	private void AddPostFxItems(List<Item> l)
	{
		PostFxConfig postFxConfig = new PostFxConfig();
		l.Add(Head("BLOOM"));
		l.Add(Bool("Bloom", () => PostFx.BloomEnabled, delegate(bool v)
		{
			PostFx.BloomEnabled = v;
		}, postFxConfig.Bloom));
		l.Add(Float("Bloom intensity", () => PostFx.BloomIntensity, delegate(float v)
		{
			PostFx.BloomIntensity = v;
		}, 0.05f, 0f, 2f, postFxConfig.BloomIntensity, "0.00"));
		l.Add(Float("Bloom threshold", () => PostFx.BloomThreshold, delegate(float v)
		{
			PostFx.BloomThreshold = v;
		}, 0.02f, 0f, 1f, postFxConfig.BloomThreshold, "0.00"));
		l.Add(Float("Bloom knee", () => PostFx.BloomKnee, delegate(float v)
		{
			PostFx.BloomKnee = v;
		}, 0.01f, 0f, 0.5f, postFxConfig.BloomKnee, "0.00"));
		l.Add(Float("Bloom emissive", () => PostFx.BloomEmissive, delegate(float v)
		{
			PostFx.BloomEmissive = v;
		}, 0.1f, 0f, 4f, postFxConfig.BloomEmissive, "0.0"));
		l.Add(Float("Bloom emissive threshold", () => PostFx.BloomEmissiveThreshold, delegate(float v)
		{
			PostFx.BloomEmissiveThreshold = v;
		}, 0.02f, 0f, 1f, postFxConfig.BloomEmissiveThreshold, "0.00"));
		l.Add(Float("Bloom radius", () => PostFx.BloomRadius, delegate(float v)
		{
			PostFx.BloomRadius = v;
		}, 0.25f, 0.25f, 8f, postFxConfig.BloomRadius, "0.00", " x"));
		l.Add(Head("AMBIENT OCCLUSION"));
		l.Add(Bool("Ambient occlusion", () => PostFx.AoEnabled, delegate(bool v)
		{
			PostFx.AoEnabled = v;
		}, postFxConfig.AmbientOcclusion));
		l.Add(Float("AO intensity", () => PostFx.AoIntensity, delegate(float v)
		{
			PostFx.AoIntensity = v;
		}, 0.05f, 0f, 1f, postFxConfig.AoIntensity, "0.00"));
		l.Add(Float("AO radius", () => PostFx.AoRadius, delegate(float v)
		{
			PostFx.AoRadius = v;
		}, 0.1f, 0.1f, 5f, postFxConfig.AoRadius, "0.0", " m"));
		l.Add(Float("AO bias", () => PostFx.AoBias, delegate(float v)
		{
			PostFx.AoBias = v;
		}, 0.01f, 0f, 0.5f, postFxConfig.AoBias, "0.00"));
		l.Add(Info("AO active", () => (!PostFx.AoEnabled || _terrain.MapView) ? "no" : "yes"));
		l.Add(Float("AO fade distance", () => PostFx.AoMaxDistance, delegate(float v)
		{
			PostFx.AoMaxDistance = v;
		}, 10f, 5f, 2000f, postFxConfig.AoMaxDistance, "0", " m"));
		l.Add(Head("GOD RAYS"));
		l.Add(Bool("God rays", () => PostFx.GodRaysEnabled, delegate(bool v)
		{
			PostFx.GodRaysEnabled = v;
		}, postFxConfig.GodRays));
		l.Add(Float("God intensity", () => PostFx.GodIntensity, delegate(float v)
		{
			PostFx.GodIntensity = v;
		}, 0.05f, 0f, 2f, postFxConfig.GodIntensity, "0.00"));
		l.Add(Float("God threshold", () => PostFx.GodThreshold, delegate(float v)
		{
			PostFx.GodThreshold = v;
		}, 0.02f, 0f, 1f, postFxConfig.GodThreshold, "0.00"));
		l.Add(Float("God decay", () => PostFx.GodDecay, delegate(float v)
		{
			PostFx.GodDecay = v;
		}, 0.01f, 0.5f, 0.995f, postFxConfig.GodDecay, "0.000"));
		l.Add(Float("God samples", () => PostFx.GodSamples, delegate(float v)
		{
			PostFx.GodSamples = (int)(v + 0.5f);
		}, 1f, 4f, 24f, postFxConfig.GodSamples, "0"));
		l.Add(Info("God active", () => (!PostFx.SunVolumetricEnabled) ? ((!PostFx.GodRaysEnabled || _terrain.MapView) ? "no" : "yes") : "no"));
		l.Add(Head("HEIGHT / DISTANCE FOG"));
		l.Add(Bool("Height fog", () => PostFx.FogEnabled, delegate(bool v)
		{
			PostFx.FogEnabled = v;
		}, postFxConfig.HeightFog));
		l.Add(Float("Fog intensity", () => PostFx.FogIntensity, delegate(float v)
		{
			PostFx.FogIntensity = v;
		}, 0.05f, 0f, 1f, postFxConfig.FogIntensity, "0.00"));
		l.Add(Float("Fog start", () => PostFx.FogStart, delegate(float v)
		{
			PostFx.FogStart = v;
		}, 10f, 0f, 5000f, postFxConfig.FogStart, "0", " m"));
		l.Add(Float("Fog end", () => PostFx.FogEnd, delegate(float v)
		{
			PostFx.FogEnd = v;
		}, 50f, 1f, 10000f, postFxConfig.FogEnd, "0", " m"));
		l.Add(Float("Fog height falloff", () => PostFx.FogHeightDensity, delegate(float v)
		{
			PostFx.FogHeightDensity = v;
		}, 0.005f, 0f, 1f, postFxConfig.FogHeightDensity, "0.000"));
		l.Add(Float("Fog base height", () => PostFx.FogHeightBase, delegate(float v)
		{
			PostFx.FogHeightBase = v;
		}, 1f, -500f, 5000f, postFxConfig.FogHeightBase, "0", " m"));
		l.Add(Head("VOLUMETRIC HEADLIGHT"));
		l.Add(Bool("Volumetric light", () => PostFx.VolumetricEnabled, delegate(bool v)
		{
			PostFx.VolumetricEnabled = v;
		}, postFxConfig.Volumetric));
		l.Add(Float("Volumetric intensity", () => PostFx.VolumetricIntensity, delegate(float v)
		{
			PostFx.VolumetricIntensity = v;
		}, 0.05f, 0f, 2f, postFxConfig.VolumetricIntensity, "0.00"));
		l.Add(Float("Volumetric steps", () => PostFx.VolumetricSteps, delegate(float v)
		{
			PostFx.VolumetricSteps = (int)(v + 0.5f);
		}, 1f, 4f, 16f, postFxConfig.VolumetricSteps, "0"));
		l.Add(Info("Volumetric active", () => (!PostFx.VolumetricEnabled || _terrain.MapView) ? "no" : "yes"));
		l.Add(Info("Player projector", () => (!PostFx.VolumetricProjectorAvailable) ? "no" : ("yes, strength " + PostFx.VolumetricProjectorStrength.ToString("0.00", Inv))));
		l.Add(Head("VOLUMETRIC SUNLIGHT"));
		l.Add(Bool("Sun volumetric", () => PostFx.SunVolumetricEnabled, delegate(bool v)
		{
			PostFx.SunVolumetricEnabled = v;
		}, postFxConfig.SunVolumetric));
		l.Add(Float("Sun volumetric intensity", () => PostFx.SunVolumetricIntensity, delegate(float v)
		{
			PostFx.SunVolumetricIntensity = v;
		}, 0.05f, 0f, 2f, postFxConfig.SunVolumetricIntensity, "0.00"));
		l.Add(Float("Sun volumetric steps", () => PostFx.SunVolumetricSteps, delegate(float v)
		{
			PostFx.SunVolumetricSteps = (int)(v + 0.5f);
		}, 1f, 4f, 24f, postFxConfig.SunVolumetricSteps, "0"));
		l.Add(Info("Sun volumetric active", () => (!PostFx.SunVolumetricActive) ? "no" : "yes"));
		l.Add(Bool("Photoshop", () => PhotoshopFx.Enabled, delegate(bool v)
		{
			PhotoshopFx.Enabled = v;
		}, def: true));
		l.Add(Table("Debug view", new string[7] { "Off", "AO buffer", "Bloom buffer", "Bloom mask", "God / sun buffer", "Fog amount", "Volumetric buffer" }, () => PostFx.DebugView, delegate(int v)
		{
			PostFx.DebugView = v;
		}, 0));
		l.Add(Info("Bloom threshold 1.0 = nothing glows"));
		l.Add(Info("AO bias high = almost no AO"));
		l.Add(Info("God threshold high = only sky glows"));
	}

	private void RenderWorldDebug(FontAtlas f, float cw, float ch, float lh, float k)
	{
		MapStreamer streamer = Streamer;
		if (streamer != null && DebugLines.Labels.Count > 0 && !_terrain.MapView)
		{
			Matrix4x4 rootMatrix = streamer.RootMatrix;
			foreach (DebugLines.Text label in DebugLines.Labels)
			{
				if (!_terrain.WorldToScreen(Vector3.Transform(label.P, rootMatrix), out var px))
				{
					continue;
				}
				string[] array = label.S.Split('\n');
				int num = 0;
				string[] array2 = array;
				foreach (string text in array2)
				{
					num = Math.Max(num, text.Length);
				}
				float num2 = (float)num * cw + 8f;
				float num3 = (float)array.Length * lh + 2f;
				float num4 = px.X - num2 * 0.5f;
				float num5 = px.Y - num3;
				if (!(num4 > (float)_r.Width) && !(num5 > (float)_r.Height) && !(num4 + num2 < 0f) && !(num5 + num3 < 0f))
				{
					Quad(num4, num5, num4 + num2, num5 + num3, 0f, 0f, 0f, 0f, CBack);
					uint col = ObjectVertex.PackColor((byte)(label.C.X * 255f), (byte)(label.C.Y * 255f), (byte)(label.C.Z * 255f), byte.MaxValue);
					for (int j = 0; j < array.Length; j++)
					{
						float num6 = px.X - (float)array[j].Length * cw * 0.5f;
						Text(f, num6, num5 + 1f + (float)j * lh, array[j], col, num6 + (float)array[j].Length * cw + 1f, cw, ch);
					}
				}
			}
		}
		Traffic traffic = Traffic;
		if (!TrafficConfig.ShowDebug || traffic == null)
		{
			return;
		}
		List<string> list = new List<string>(traffic.DebugPanel());
		int num7 = 0;
		foreach (string item in list)
		{
			num7 = Math.Max(num7, item.Length);
		}
		float num8 = MathF.Min((float)_r.Width - 16f * k, (float)num7 * cw + 16f);
		float num9 = (float)list.Count * lh + 10f;
		float num10 = 8f * k;
		float num11 = (float)_r.Height - num9 - 8f * k;
		Quad(num10, num11, num10 + num8, num11 + num9, 0f, 0f, 0f, 0f, CBack);
		for (int l = 0; l < list.Count; l++)
		{
			Text(f, num10 + 6f, num11 + 5f + (float)l * lh, list[l], CText, num10 + num8 - 6f, cw, ch);
		}
	}

	private static System.Numerics.Vector4 Col(int i)
	{
		return CollisionColors[Math.Clamp(i, 0, CollisionColors.Length - 1)];
	}

	private static System.Numerics.Vector4 Dim(System.Numerics.Vector4 c)
	{
		return new System.Numerics.Vector4(c.X * 0.6f, c.Y * 0.6f, c.Z * 0.6f, 1f);
	}

	private static string ColName(int i)
	{
		return CollisionColorNames[Math.Clamp(i, 0, CollisionColorNames.Length - 1)];
	}

	private void SetCollisionPreset(int i)
	{
		_colPreset = (i % CollisionPresets.Length + CollisionPresets.Length) % CollisionPresets.Length;
		int[] array = CollisionPresets[_colPreset];
		_colRoad = array[0];
		_colObjects = array[1];
		_colProps = array[2];
		_colFixed = array[3];
		_colLoose = array[4];
		_colCar = array[5];
		_colPath = array[6];
	}

	private void AddCollisionDebugToggles(List<Item> l)
	{
		l.Add(Bool("Show collision boxes", () => _collisionBoxes, delegate(bool v)
		{
			_collisionBoxes = v;
		}, def: false));
		l.Add(Float("Collision box range", () => _collisionRange, delegate(float v)
		{
			_collisionRange = MathF.Max(20f, v);
		}, 25f, 20f, 1000f, 100f, "0", " m"));
		l.Add(Float("Detail range", () => _detailDistance, delegate(float v)
		{
			_detailDistance = MathF.Max(10f, v);
		}, 5f, 10f, 500f, 45f, "0", " m"));
		l.Add(Info("Visible road / sector / props", () => _cntRoad + " / " + _cntObjects + " / " + _cntProps));
		l.Add(Info("Visible fixed / loose / cars", () => _cntFixed + " / " + _cntLoose + " / " + _cntCar));
		l.Add(Info("Types / colors: Debug > Collision view"));
	}

	private List<Item> CollisionViewMenu()
	{
		return new List<Item>
		{
			Head("QUICK GROUPS"),
			Action_("All collision groups ON", delegate
			{
				SetAllCollisionGroups(on: true);
			}),
			Action_("All collision groups OFF", delegate
			{
				SetAllCollisionGroups(on: false);
			}),
			Head("WORLD COLLIDERS"),
			Bool("Road / terrain", () => _boxRoad, delegate(bool v)
			{
				_boxRoad = v;
			}, def: true),
			Bool("Sector objects", () => _boxObjects, delegate(bool v)
			{
				_boxObjects = v;
			}, def: true),
			Bool("Props & guardrails", () => _boxProps, delegate(bool v)
			{
				_boxProps = v;
			}, def: true),
			Head("ACTORS"),
			Bool("Fixed actors", () => _boxFixed, delegate(bool v)
			{
				_boxFixed = v;
			}, def: true),
			Bool("Breakable actors", () => _boxLoose, delegate(bool v)
			{
				_boxLoose = v;
			}, def: true),
			Head("VEHICLES"),
			Bool("Cars", () => _boxCars, delegate(bool v)
			{
				_boxCars = v;
			}, def: true),
			Head("OVERLAYS"),
			Bool("Box labels", () => _boxLabels, delegate(bool v)
			{
				_boxLabels = v;
			}, def: true),
			Bool("AI path system", () => _aiPaths, delegate(bool v)
			{
				_aiPaths = v;
			}, def: false),
			Head("COLOR PRESET"),
			Table("Preset", CollisionPresetNames, () => _colPreset, SetCollisionPreset, 0),
			Head("PER GROUP COLORS"),
			Table("Road / terrain", CollisionColorNames, () => _colRoad, delegate(int v)
			{
				_colRoad = v;
			}, 0),
			Table("Sector objects", CollisionColorNames, () => _colObjects, delegate(int v)
			{
				_colObjects = v;
			}, 8),
			Table("Props & guardrails", CollisionColorNames, () => _colProps, delegate(int v)
			{
				_colProps = v;
			}, 5),
			Table("Fixed actors", CollisionColorNames, () => _colFixed, delegate(int v)
			{
				_colFixed = v;
			}, 1),
			Table("Breakable actors", CollisionColorNames, () => _colLoose, delegate(int v)
			{
				_colLoose = v;
			}, 2),
			Table("Cars", CollisionColorNames, () => _colCar, delegate(int v)
			{
				_colCar = v;
			}, 3),
			Info("AI path", () => ColName(_colPath)),
			Head("DETAIL BUDGET"),
			Float("Detail triangles", () => (float)_detailTriBudget / 1000f, delegate(float v)
			{
				_detailTriBudget = (int)(v * 1000f);
			}, 5f, 5f, 250f, 60f, "0", " k"),
			Int("Max box labels", () => _maxCollisionLabels, delegate(int v)
			{
				_maxCollisionLabels = v;
			}, 0, 200, 48),
			Info("AI path lines", () => _pathLineBudget.ToString()),
			Head("LEGEND"),
			Info("Road / terrain", () => ColName(_colRoad)),
			Info("Sector objects", () => ColName(_colObjects)),
			Info("Props & guardrails", () => ColName(_colProps)),
			Info("Fixed actors", () => ColName(_colFixed)),
			Info("Breakable actors", () => ColName(_colLoose)),
			Info("Cars", () => ColName(_colCar)),
			Info("AI path", () => ColName(_colPath)),
			Info("Far colliders", () => "dim group outline"),
			Info("AI path lines visible", () => _cntPath.ToString())
		};
	}

	private void SetAllCollisionGroups(bool on)
	{
		_boxRoad = (_boxObjects = (_boxProps = (_boxFixed = (_boxLoose = (_boxCars = on)))));
		Toast(on ? "Collision groups: all ON" : "Collision groups: all OFF");
	}

	private void AddAiPathToggles(List<Item> l)
	{
		l.Add(Bool("AI path system", () => _aiPaths, delegate(bool v)
		{
			_aiPaths = v;
		}, def: false));
		l.Add(Info("AI path lines drawn", () => _cntPath.ToString()));
	}

	private List<Item> AiPathMenu()
	{
		return new List<Item>
		{
			Head("AI PATH SYSTEM"),
			Bool("Draw AI path lines", () => _aiPaths, delegate(bool v)
			{
				_aiPaths = v;
			}, def: false),
			Table("Path color", CollisionColorNames, () => _colPath, delegate(int v)
			{
				_colPath = v;
			}, 10),
			Int("Line budget", () => _pathLineBudget, delegate(int v)
			{
				_pathLineBudget = v;
			}, 100, 10000, 1500),
			Float("Range", () => _collisionRange, delegate(float v)
			{
				_collisionRange = MathF.Max(20f, v);
			}, 25f, 20f, 1000f, 100f, "0", " m"),
			Info("Lines visible", () => _cntPath.ToString()),
			Info("Lane network", () => (Traffic?.Lanes == null) ? "-" : "loaded"),
			Head(""),
			Info("Same lane polylines that traffic and race opponents follow."),
			Info("Also reachable from Collision Debug View and Debug Views hub.")
		};
	}

	private static float DistSqToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
	{
		Vector3 vector = b - a;
		Vector3 vector2 = c - a;
		Vector3 vector3 = p - a;
		float num = Vector3.Dot(vector, vector3);
		float num2 = Vector3.Dot(vector2, vector3);
		if (num <= 0f && num2 <= 0f)
		{
			return vector3.LengthSquared();
		}
		Vector3 vector4 = p - b;
		float num3 = Vector3.Dot(vector, vector4);
		float num4 = Vector3.Dot(vector2, vector4);
		if (num3 >= 0f && num4 <= num3)
		{
			return vector4.LengthSquared();
		}
		float num5 = num * num4 - num3 * num2;
		if (num5 <= 0f && num >= 0f && num3 <= 0f)
		{
			float num6 = num / (num - num3);
			return (vector3 - vector * num6).LengthSquared();
		}
		Vector3 vector5 = p - c;
		float num7 = Vector3.Dot(vector, vector5);
		float num8 = Vector3.Dot(vector2, vector5);
		if (num8 >= 0f && num7 <= num8)
		{
			return vector5.LengthSquared();
		}
		float num9 = num7 * num2 - num * num8;
		if (num9 <= 0f && num2 >= 0f && num8 <= 0f)
		{
			float num10 = num2 / (num2 - num8);
			return (vector3 - vector2 * num10).LengthSquared();
		}
		float num11 = num3 * num8 - num7 * num4;
		if (num11 <= 0f && num4 - num3 >= 0f && num7 - num8 >= 0f)
		{
			float num12 = num4 - num3 + (num7 - num8);
			float num13 = ((num12 > 1E-12f) ? ((num4 - num3) / num12) : 0f);
			return (p - (b + (c - b) * num13)).LengthSquared();
		}
		float num14 = num11 + num9 + num5;
		if (MathF.Abs(num14) < 1E-12f)
		{
			return vector3.LengthSquared();
		}
		float num15 = 1f / num14;
		return (p - (a + vector * (num9 * num15) + vector2 * (num5 * num15))).LengthSquared();
	}

	private static int DrawMeshTriangles(PhysicsWorld w, StaticCollider c, Vector3 eye, float r2, System.Numerics.Vector4 color, int budget, int from = 0, int count = -1)
	{
		if (budget <= 0)
		{
			return 0;
		}
		if (!(w.Simulation.Shapes[c.Shape.Type] is ShapeBatch<Mesh> shapeBatch))
		{
			return 0;
		}
		StaticReference staticReference = w.Simulation.Statics[c.Handle];
		Vector3 vector = w.ToGame(staticReference.Pose.Position);
		Quaternion orientation = staticReference.Pose.Orientation;
		Buffer<Triangle> triangles = shapeBatch[c.Shape.Index].Triangles;
		int num = ((count < 0) ? triangles.Length : Math.Min(triangles.Length, from + count));
		int num2 = 0;
		for (int i = from; i < num; i++)
		{
			if (num2 >= budget)
			{
				break;
			}
			Triangle triangle = triangles[i];
			Vector3 vector2 = vector + Vector3.Transform(triangle.A, orientation);
			Vector3 vector3 = vector + Vector3.Transform(triangle.B, orientation);
			Vector3 vector4 = vector + Vector3.Transform(triangle.C, orientation);
			if (!(DistSqToTriangle(eye, vector2, vector3, vector4) > r2))
			{
				DebugLines.Line(vector2, vector3, color);
				DebugLines.Line(vector3, vector4, color);
				DebugLines.Line(vector4, vector2, color);
				num2++;
			}
		}
		return num2;
	}

	private void Label(Vector3 at, string text, int colorIndex)
	{
		if (_boxLabels && _labelsThisFrame < _maxCollisionLabels)
		{
			_labelsThisFrame++;
			DebugLines.Label(at, text, Col(colorIndex));
		}
	}

	private void DrawFlatPatch(StaticCollider c, Vector3 eye, float cap, int colorIndex, string text)
	{
		Vector3 value = c.BoundsCenter - c.BoundsHalf;
		Vector3 vector = c.BoundsCenter + c.BoundsHalf;
		Vector3 value2 = new Vector3(MathF.Floor(eye.X / cap) * cap, 0f, MathF.Floor(eye.Z / cap) * cap);
		Vector3 vector2 = Vector3.Max(value, Vector3.Min(value2, vector));
		Vector3 vector3 = new Vector3(cap);
		Vector3 vector4 = Vector3.Max(value, vector2 - vector3);
		Vector3 vector5 = Vector3.Min(vector, vector2 + vector3);
		Vector3 vector6 = (vector4 + vector5) * 0.5f;
		Vector3 size = vector5 - vector4;
		if (!(size.X < 0.02f) && !(size.Z < 0.02f))
		{
			size.X += 0.001f;
			size.Z += 0.001f;
			vector6.Y = vector2.Y + 0.02f;
			size.Y = 0.15f;
			DebugLines.Box(vector6, Quaternion.Identity, size, Col(colorIndex));
			Label(vector6, text, colorIndex);
		}
	}

	private void CollectAiPathDebug()
	{
		_cntPath = 0;
		if (!_aiPaths)
		{
			return;
		}
		Traffic traffic = Traffic;
		MapStreamer streamer = Streamer;
		if (traffic?.Lanes == null || streamer == null)
		{
			return;
		}
		TrafficLanes lanes = traffic.Lanes;
		Vector3 viewerGame = streamer.ViewerGame;
		float num = MathF.Max(20f, _collisionRange);
		float num2 = num * num;
		System.Numerics.Vector4 c = Col(_colPath);
		int num3 = _pathLineBudget;
		foreach (int item in lanes.RoadsNear(viewerGame, num))
		{
			if (!lanes.Net.Roads.TryGetValue(item, out IaRoad value))
			{
				continue;
			}
			for (int i = 0; i < 2; i++)
			{
				if (num3 <= 0)
				{
					break;
				}
				bool forward = i == 0;
				int num4 = lanes.LaneCount(value, forward);
				for (int j = 0; j < num4; j++)
				{
					if (num3 <= 0)
					{
						break;
					}
					LaneTrack laneTrack = lanes.Lane(item, forward, j);
					if (laneTrack == null || laneTrack.P.Length < 2)
					{
						continue;
					}
					for (int k = 0; k + 1 < laneTrack.P.Length; k++)
					{
						if (num3 <= 0)
						{
							break;
						}
						Vector3 vector = laneTrack.P[k];
						Vector3 vector2 = laneTrack.P[k + 1];
						if (!((vector - viewerGame).LengthSquared() > num2) || !((vector2 - viewerGame).LengthSquared() > num2))
						{
							DebugLines.Line(vector + Vector3.UnitY * 0.25f, vector2 + Vector3.UnitY * 0.25f, c);
							num3--;
							_cntPath++;
						}
					}
				}
			}
			if (num3 <= 0)
			{
				break;
			}
		}
	}

	private void CollectCollisionDebug()
	{
		_labelsThisFrame = 0;
		CollectAiPathDebug();
		if (!_collisionBoxes || _terrain.MapView || (!_boxRoad && !_boxObjects && !_boxProps && !_boxFixed && !_boxLoose && !_boxCars))
		{
			_cntRoad = (_cntObjects = (_cntProps = (_cntFixed = (_cntLoose = (_cntCar = 0)))));
			return;
		}
		VehicleManager mgr = Mgr;
		MapStreamer streamer = Streamer;
		if (mgr == null || streamer == null)
		{
			return;
		}
		Vector3 eye = streamer.ViewerGame;
		float num = MathF.Max(20f, _collisionRange);
		float r2 = num * num;
		_cntRoad = (_cntObjects = (_cntProps = (_cntFixed = (_cntLoose = (_cntCar = 0)))));
		PhysicsWorld physics = streamer.Physics;
		if (physics != null && (_boxRoad || _boxObjects || _boxProps))
		{
			float cap = MathF.Min(num * 0.5f, 20f);
			float num2 = MathF.Min(_detailDistance, num);
			float num3 = num2 * num2;
			List<StaticDraw> list = new List<StaticDraw>(128);
			foreach (StaticCollider @static in physics.Statics)
			{
				if (@static.Removed || @static.Name.Contains(" Actor", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				bool flag = @static.Name.EndsWith(" Terrain", StringComparison.Ordinal);
				bool flag2 = @static.Name.EndsWith(" Objects", StringComparison.Ordinal);
				bool num4;
				if (!flag)
				{
					if (_boxObjects && flag2)
					{
						goto IL_0208;
					}
					if (!_boxProps)
					{
						continue;
					}
					num4 = flag2;
				}
				else
				{
					num4 = !_boxRoad;
				}
				if (num4)
				{
					continue;
				}
				goto IL_0208;
				IL_0208:
				Vector3 vector = @static.BoundsCenter - @static.BoundsHalf;
				Vector3 vector2 = @static.BoundsCenter + @static.BoundsHalf;
				float num5 = MathF.Max(0f, MathF.Max(vector.X - eye.X, eye.X - vector2.X));
				float num6 = MathF.Max(0f, MathF.Max(vector.Y - eye.Y, eye.Y - vector2.Y));
				float num7 = MathF.Max(0f, MathF.Max(vector.Z - eye.Z, eye.Z - vector2.Z));
				float num8 = num5 * num5 + num6 * num6 + num7 * num7;
				if (num8 > r2)
				{
					continue;
				}
				if (flag)
				{
					list.Add(new StaticDraw
					{
						Collider = @static,
						Center = @static.BoundsCenter,
						Half = @static.BoundsHalf,
						DistSq = num8,
						ColorIndex = _colRoad,
						Flat = true,
						Label = "Sector: terrain/road"
					});
				}
				else if (flag2)
				{
					PhysicsWorld.PreparedMesh.DebugPart[] debugParts = @static.DebugParts;
					if (debugParts != null && debugParts.Length > 0)
					{
						PhysicsWorld.PreparedMesh.DebugPart[] debugParts2 = @static.DebugParts;
						for (int i = 0; i < debugParts2.Length; i++)
						{
							PhysicsWorld.PreparedMesh.DebugPart debugPart = debugParts2[i];
							Vector3 vector3 = debugPart.Center - debugPart.Half;
							Vector3 vector4 = debugPart.Center + debugPart.Half;
							float num9 = MathF.Max(0f, MathF.Max(vector3.X - eye.X, eye.X - vector4.X));
							float num10 = MathF.Max(0f, MathF.Max(vector3.Y - eye.Y, eye.Y - vector4.Y));
							float num11 = MathF.Max(0f, MathF.Max(vector3.Z - eye.Z, eye.Z - vector4.Z));
							float num12 = num9 * num9 + num10 * num10 + num11 * num11;
							if (!(num12 > r2))
							{
								list.Add(new StaticDraw
								{
									Collider = @static,
									Center = debugPart.Center,
									Half = debugPart.Half,
									DistSq = num12,
									From = debugPart.Start * 2,
									Count = debugPart.Count * 2,
									ColorIndex = _colObjects,
									Label = "Sector object"
								});
							}
						}
					}
					else
					{
						list.Add(new StaticDraw
						{
							Collider = @static,
							Center = @static.BoundsCenter,
							Half = @static.BoundsHalf,
							DistSq = num8,
							ColorIndex = _colObjects,
							Flat = true,
							Label = "Sector: objects"
						});
					}
				}
				else
				{
					list.Add(new StaticDraw
					{
						Collider = @static,
						Center = @static.BoundsCenter,
						Half = @static.BoundsHalf,
						DistSq = num8,
						From = 0,
						Count = -1,
						ColorIndex = _colProps,
						Label = @static.Name
					});
				}
			}
			list.Sort((StaticDraw a, StaticDraw b) => a.DistSq.CompareTo(b.DistSq));
			int num13 = _detailTriBudget;
			foreach (StaticDraw item in list)
			{
				int colorIndex = item.ColorIndex;
				if (item.Flat)
				{
					bool flag3 = item.Label.StartsWith("Sector: terrain", StringComparison.Ordinal);
					DrawFlatPatch(item.Collider, eye, cap, colorIndex, item.Label);
					if (flag3)
					{
						_cntRoad++;
					}
					else
					{
						_cntObjects++;
					}
					continue;
				}
				int num14 = ((item.Count < 0) ? item.Collider.Triangles : item.Count);
				if (item.DistSq <= num3 && num14 > 0 && num14 <= num13)
				{
					int num15 = DrawMeshTriangles(physics, item.Collider, eye, r2, Col(colorIndex), num14, item.From, item.Count);
					num13 -= num15;
					if (num15 > 0)
					{
						Label(item.Center, item.Label, colorIndex);
						if (item.Count < 0)
						{
							_cntProps++;
						}
						else
						{
							_cntObjects++;
						}
						continue;
					}
				}
				Vector3 vector5 = Vector3.Max(item.Half, new Vector3(0.02f));
				DebugLines.Box(item.Center, Quaternion.Identity, vector5 * 2f, Dim(Col(colorIndex)));
				if (item.Count < 0)
				{
					_cntProps++;
				}
				else
				{
					_cntObjects++;
				}
			}
		}
		WorldActorBodies current3 = WorldActorBodies.Current;
		if (current3 != null && (_boxLoose || _boxFixed))
		{
			foreach (var item2 in current3.DebugBoxes())
			{
				if (!((item2.center - eye).LengthSquared() > r2) && (!item2.breakable || _boxLoose) && (item2.breakable || _boxFixed))
				{
					DebugLines.Box(item2.center, item2.rot, item2.size, Col(item2.breakable ? _colLoose : _colFixed));
					Label(item2.center, item2.breakable ? "Actor: knock-over" : "Actor: fixed", item2.breakable ? _colLoose : _colFixed);
					if (item2.breakable)
					{
						_cntLoose++;
					}
					else
					{
						_cntFixed++;
					}
				}
			}
		}
		Vehicle current5 = mgr.Current;
		VehicleRigidbody vehicleRigidbody = current5?.Rigidbody;
		if (_boxCars && current5 != null && vehicleRigidbody != null && vehicleRigidbody.Created)
		{
			VehiclePose visualPose = current5.VisualPose;
			bool flag4 = true;
			foreach (var debugBox in vehicleRigidbody.DebugBoxes)
			{
				Vector3 vector6 = mgr.ToGame(visualPose.position + U.Rot(visualPose.rotation, debugBox.center));
				if ((vector6 - eye).LengthSquared() <= r2)
				{
					DebugLines.Box(vector6, U.Mirror(visualPose.rotation), debugBox.size, Col(_colCar));
					if (flag4)
					{
						Label(vector6, "Player car", _colCar);
						flag4 = false;
					}
					_cntCar++;
				}
			}
		}
		if (_boxCars)
		{
			Traffic traffic = Traffic;
			if (traffic != null)
			{
				Cars(traffic.DebugBoxes(), "Traffic");
			}
			Cars(RemoteCars.DebugBoxes(), "Online");
			RaceOpponents active = RaceOpponents.Active;
			if (active != null)
			{
				Cars(active.DebugBoxes(), "Race");
			}
		}
		void Cars(IEnumerable<(Vector3 center, Quaternion rot, Vector3 size)> boxes, string name)
		{
			foreach (var box in boxes)
			{
				if ((box.center - eye).LengthSquared() <= r2)
				{
					DebugLines.Box(box.center, box.rot, box.size, Col(_colCar));
					Label(box.center, name, _colCar);
					_cntCar++;
				}
			}
		}
	}

	public DevMenu(IRenderer renderer, TerrainModule terrain, VehicleModule vehicles, IGameWindow window, LaunchOptions opts)
	{
		_r = renderer;
		_terrain = terrain;
		_vehicles = vehicles;
		_window = window;
		_opts = opts;
		_gpu = new GpuMonitor(renderer.Stats.DeviceName, renderer.Stats.VendorId, renderer.Stats.DeviceId);
	}

	public override void Update(in GameTime time)
	{
		_time = time.TotalTime;
		if (_time >= _nextGpuSample)
		{
			_nextGpuSample = _time + 0.5;
			_gpu.Sample();
		}
		FlashBrowser.Pump();
		EditorUpdate();
		SceneEditor.Pump();
		SampleFrame(time.DeltaTime);
		CollectCarDebug();
		CollectCollisionDebug();
		CollectRaceDebug();
		UpdateTools(time.DeltaTime);
		if (TDU2Controls.RawKeyPressed(112))
		{
			if (_open)
			{
				Close();
			}
			else
			{
				Open();
			}
		}
		else
		{
			if (!_open)
			{
				return;
			}
			if (ToggleKeyPressed())
			{
				SetDrive(!_drive);
			}
			if (_drive)
			{
				if (_blockedByMe)
				{
					TDU2Controls.Blocked = false;
					_blockedByMe = false;
				}
				Rebuild();
				return;
			}
			if (!TDU2Controls.Blocked)
			{
				TDU2Controls.Blocked = true;
				_blockedByMe = true;
			}
			Rebuild();
			UpdatePanelMove();
			List<Page> stack = _stack;
			Page page = stack[stack.Count - 1];
			bool flag = CtrlDown() && !_drive;
			bool flag2 = TDU2Controls.RawKeyDown(16) || TDU2Controls.RawKeyDown(160) || TDU2Controls.RawKeyDown(161);
			if (!flag)
			{
				if (KeyRepeat(40))
				{
					Move(page, 1);
				}
				if (KeyRepeat(38))
				{
					Move(page, -1);
				}
				if (KeyRepeat(34))
				{
					Move(page, 10);
				}
				if (KeyRepeat(33))
				{
					Move(page, -10);
				}
				if (TDU2Controls.RawKeyPressed(36))
				{
					page.Sel = 0;
					Move(page, 0);
				}
				if (TDU2Controls.RawKeyPressed(35))
				{
					page.Sel = _items.Count - 1;
					Move(page, 0);
				}
				Item item = ((page.Sel >= 0 && page.Sel < _items.Count) ? _items[page.Sel] : null);
				if (item != null && !item.Header)
				{
					if (KeyRepeat(39))
					{
						item.Change?.Invoke((!flag2) ? 1 : 10);
					}
					if (KeyRepeat(37))
					{
						item.Change?.Invoke(flag2 ? (-10) : (-1));
					}
					if (TDU2Controls.RawKeyPressed(46))
					{
						item.Reset?.Invoke();
					}
					if (TDU2Controls.RawKeyPressed(13))
					{
						if (item.Sub != null)
						{
							_stack.Add(new Page
							{
								Title = item.Label,
								Build = item.Sub
							});
						}
						else if (item.Activate != null)
						{
							item.Activate();
						}
						else
						{
							item.Change?.Invoke(1);
						}
					}
				}
			}
			if (TDU2Controls.RawKeyPressed(8))
			{
				if (_stack.Count > 1)
				{
					_stack.RemoveAt(_stack.Count - 1);
				}
				else
				{
					Close();
				}
			}
		}
	}

	private static bool ToggleKeyPressed()
	{
		TDU2Controls.Action action = TDU2Controls.Get("dev_menuctrl");
		if (action != null)
		{
			if (!TDU2Controls.RawKeyPressed(action.Key1))
			{
				return TDU2Controls.RawKeyPressed(action.Key2);
			}
			return true;
		}
		return false;
	}

	private static string ToggleKeyName()
	{
		TDU2Controls.Action action = TDU2Controls.Get("dev_menuctrl");
		if (action == null || action.Key1 <= 0)
		{
			return "-";
		}
		return TDU2Controls.KeyName(action.Key1);
	}

	private static bool CtrlDown()
	{
		if (!TDU2Controls.RawKeyDown(17) && !TDU2Controls.RawKeyDown(162))
		{
			return TDU2Controls.RawKeyDown(163);
		}
		return true;
	}

	private void UpdatePanelMove()
	{
		float num = ((_window.Width > 0) ? ((float)_r.Width / (float)_window.Width) : 1f);
		float num2 = ((_window.Height > 0) ? ((float)_r.Height / (float)_window.Height) : 1f);
		if (CtrlDown() && !_drive)
		{
			float num3 = (TDU2Controls.RawKeyDown(16) ? 50f : 12f);
			bool flag = false;
			if (KeyRepeat(37))
			{
				_panelNX -= num3 * num / MathF.Max(1f, _slideX);
				flag = true;
			}
			if (KeyRepeat(39))
			{
				_panelNX += num3 * num / MathF.Max(1f, _slideX);
				flag = true;
			}
			if (KeyRepeat(38))
			{
				_panelNY -= num3 * num2 / MathF.Max(1f, _slideY);
				flag = true;
			}
			if (KeyRepeat(40))
			{
				_panelNY += num3 * num2 / MathF.Max(1f, _slideY);
				flag = true;
			}
			if (TDU2Controls.RawKeyPressed(36))
			{
				ResetPanel();
				flag = true;
			}
			if (flag)
			{
				_dragging = false;
				return;
			}
		}
		InputState input = _window.Input;
		if (_dragging && !input.LeftMouseDown)
		{
			_dragging = false;
		}
		if (!_dragging && input.LeftMousePressed && !CtrlDown())
		{
			if (input.CursorLocked)
			{
				_dragging = true;
			}
			else
			{
				Paradise.Core.Flash.Vector2 mousePosition = FlashInput.mousePosition;
				int width = Screen.width;
				int height = Screen.height;
				if (width > 0 && height > 0)
				{
					float num4 = mousePosition.x * (float)_r.Width / (float)width;
					float num5 = ((float)height - mousePosition.y) * (float)_r.Height / (float)height;
					_dragging = num4 >= _panelRx && num4 <= _panelRx + _panelW && num5 >= _panelRy && num5 <= _panelRy + _panelHeaderH;
				}
			}
		}
		if (_dragging)
		{
			_panelNX += input.MouseDeltaX * num / MathF.Max(1f, _slideX);
			_panelNY += input.MouseDeltaY * num2 / MathF.Max(1f, _slideY);
		}
		_panelNX = Math.Clamp(_panelNX, 0f, 1f);
		_panelNY = Math.Clamp(_panelNY, 0f, 1f);
	}

	private void ResetPanel()
	{
		_panelNX = (_panelNY = 0f);
		_dragging = false;
		Toast("Dev menu position reset");
	}

	private void SetDrive(bool on)
	{
		_drive = on;
		if (on)
		{
			if (_blockedByMe)
			{
				TDU2Controls.Blocked = false;
				_blockedByMe = false;
			}
		}
		else if (_open && !TDU2Controls.Blocked)
		{
			TDU2Controls.Blocked = true;
			_blockedByMe = true;
		}
		Toast(on ? ("Toggle Control: driving " + ToggleKeyName() + " = back to the menu") : "Toggle Control: menu navigation");
	}

	private bool KeyRepeat(int vk)
	{
		if (TDU2Controls.RawKeyPressed(vk))
		{
			_repeatKey = vk;
			_repeatAt = _time + 0.4;
			return true;
		}
		if (_repeatKey == vk && TDU2Controls.RawKeyDown(vk) && _time >= _repeatAt)
		{
			_repeatAt = _time + 0.06;
			return true;
		}
		return false;
	}

	private void Move(Page p, int d)
	{
		if (_items.Count == 0)
		{
			p.Sel = 0;
			return;
		}
		int i = Math.Clamp(p.Sel + d, 0, _items.Count - 1);
		for (int num = ((d >= 0) ? 1 : (-1)); i >= 0 && i < _items.Count && _items[i].Header; i += num)
		{
		}
		if (i < 0 || i >= _items.Count)
		{
			i = Math.Clamp(p.Sel, 0, _items.Count - 1);
		}
		p.Sel = i;
	}

	private void Open()
	{
		_open = true;
		_stack.Clear();
		_stack.Add(new Page
		{
			Title = "GAME build: 0.1.2",
			Build = MainMenu
		});
		if (!_drive && !TDU2Controls.Blocked)
		{
			TDU2Controls.Blocked = true;
			_blockedByMe = true;
		}
		Rebuild();
		Move(_stack[0], 0);
	}

	private void Close()
	{
		_open = false;
		if (_blockedByMe)
		{
			TDU2Controls.Blocked = false;
			_blockedByMe = false;
		}
	}

	private void Rebuild()
	{
		List<Page> stack = _stack;
		Page page = stack[stack.Count - 1];
		try
		{
			_items = page.Build() ?? new List<Item>();
		}
		catch (Exception ex)
		{
			_items = new List<Item> { Info("Error: " + ex.Message) };
		}
		if (page.Sel >= _items.Count)
		{
			page.Sel = Math.Max(0, _items.Count - 1);
		}
		if (_items.Count > 0 && _items[page.Sel].Header)
		{
			Move(page, 1);
		}
	}

	private void Toast(string s)
	{
		_toast = s;
		_toastUntil = _time + 3.0;
	}

	public override void Unload()
	{
		CutsceneTester.Instance?.Unload();
		AnimViewer.Instance?.Close();
		Close();
		_font?.Dispose();
		_font = null;
		_gpu.Dispose();
	}

	private static Item Head(string t)
	{
		return new Item
		{
			Label = t,
			Header = true
		};
	}

	private static Item Info(string label, Func<string>? v = null)
	{
		return new Item
		{
			Label = label,
			Value = v,
			Header = true
		};
	}

	private static Item Bool(string label, Func<bool> get, Action<bool> set, bool def)
	{
		Func<bool> get2 = get;
		Action<bool> set2 = set;
		return new Item
		{
			Label = label,
			Value = () => (!get2()) ? "OFF" : "ON",
			Change = delegate
			{
				set2(!get2());
			},
			Reset = delegate
			{
				set2(def);
			}
		};
	}

	private static Item Float(string label, Func<float> get, Action<float> set, float step, float min, float max, float def, string fmt = "0.##", string unit = "")
	{
		Func<float> get2 = get;
		string fmt2 = fmt;
		string unit2 = unit;
		Action<float> set2 = set;
		return new Item
		{
			Label = label,
			Value = () => get2().ToString(fmt2, CultureInfo.InvariantCulture) + unit2,
			Change = delegate(int d)
			{
				set2(Math.Clamp(get2() + step * (float)d, min, max));
			},
			Reset = delegate
			{
				set2(def);
			}
		};
	}

	private static Item Int(string label, Func<int> get, Action<int> set, int min, int max, int def)
	{
		Func<int> get2 = get;
		Action<int> set2 = set;
		return new Item
		{
			Label = label,
			Value = () => get2().ToString(CultureInfo.InvariantCulture),
			Change = delegate(int d)
			{
				set2(Math.Clamp(get2() + d, min, max));
			},
			Reset = delegate
			{
				set2(def);
			}
		};
	}

	private static Item Table(string label, string[] names, Func<int> get, Action<int> set, int def)
	{
		Func<int> get2 = get;
		string[] names2 = names;
		Action<int> set2 = set;
		return new Item
		{
			Label = label,
			Value = delegate
			{
				int num = get2();
				return (num < 0 || num >= names2.Length) ? num.ToString(CultureInfo.InvariantCulture) : names2[num];
			},
			Change = delegate(int d)
			{
				set2(((get2() + Math.Sign(d)) % names2.Length + names2.Length) % names2.Length);
			},
			Reset = delegate
			{
				set2(def);
			}
		};
	}

	private static Item Action_(string label, Action a)
	{
		return new Item
		{
			Label = label,
			Activate = a,
			Value = () => "[Enter]"
		};
	}

	private static Item Menu(string label, Func<List<Item>> sub)
	{
		return new Item
		{
			Label = label,
			Sub = sub,
			Value = () => "->"
		};
	}

	private static Item PhInfo(string label)
	{
		return PhInfo(label, "n/a");
	}

	private static Item PhInfo(string label, string value)
	{
		string value2 = value;
		return new Item
		{
			Label = label,
			Value = () => value2,
			Header = true,
			Placeholder = true
		};
	}

	private Item PhAction(string label, string? hint = null)
	{
		string hint2 = hint;
		string label2 = label;
		return new Item
		{
			Label = label2,
			Value = () => "[Enter]",
			Placeholder = true,
			Activate = delegate
			{
				Toast(hint2 ?? (label2 + ": not available in the engine yet"));
			}
		};
	}

	private Item PhBool(string label, bool def)
	{
		string label2 = label;
		return new Item
		{
			Label = label2,
			Placeholder = true,
			Value = () => (!PhGet(label2, def)) ? "OFF" : "ON",
			Change = delegate
			{
				PhSet(label2, !PhGet(label2, def));
			},
			Reset = delegate
			{
				PhSet(label2, def);
			}
		};
	}

	private Item PhFloat(string label, float step, float min, float max, float def, string fmt = "0.##", string unit = "")
	{
		string label2 = label;
		string fmt2 = fmt;
		string unit2 = unit;
		return new Item
		{
			Label = label2,
			Placeholder = true,
			Value = () => PhGet(label2, def).ToString(fmt2, CultureInfo.InvariantCulture) + unit2,
			Change = delegate(int d)
			{
				PhSet(label2, Math.Clamp(PhGet(label2, def) + step * (float)d, min, max));
			},
			Reset = delegate
			{
				PhSet(label2, def);
			}
		};
	}

	private Item PhInt(string label, int min, int max, int def)
	{
		string label2 = label;
		return new Item
		{
			Label = label2,
			Placeholder = true,
			Value = () => PhGet(label2, def).ToString(CultureInfo.InvariantCulture),
			Change = delegate(int d)
			{
				PhSet(label2, Math.Clamp(PhGet(label2, def) + d, min, max));
			},
			Reset = delegate
			{
				PhSet(label2, def);
			}
		};
	}

	private Item PhTable(string label, string[] names, int def)
	{
		string[] names2 = names;
		string label2 = label;
		return new Item
		{
			Label = label2,
			Placeholder = true,
			Value = () => names2[Math.Clamp(PhGet(label2, def), 0, names2.Length - 1)],
			Change = delegate(int d)
			{
				int num = PhGet(label2, def);
				PhSet(label2, ((num + Math.Sign(d)) % names2.Length + names2.Length) % names2.Length);
			},
			Reset = delegate
			{
				PhSet(label2, def);
			}
		};
	}

	private static Item PhMenu(string label, string hint)
	{
		string label2 = label;
		string hint2 = hint;
		return new Item
		{
			Label = label2,
			Value = () => ">>",
			Placeholder = true,
			Sub = () => new List<Item>
			{
				Head(label2.ToUpperInvariant()),
				PhInfo(hint2)
			}
		};
	}

	private T PhGet<T>(string key, T def)
	{
		if (_ph.TryGetValue(key, out object value) && value is T)
		{
			return (T)value;
		}
		return def;
	}

	private void PhSet(string key, object v)
	{
		_ph[key] = v;
	}

	private void SampleFrame(float dt)
	{
		if (!(dt <= 0f))
		{
			_sampleFrames++;
			_sampleTime += dt;
			_sampleMin = MathF.Min(_sampleMin, dt);
			_sampleMax = MathF.Max(_sampleMax, dt);
			if (_sampleTime >= 0.5f)
			{
				_fps = (float)_sampleFrames / _sampleTime;
				_ftAvg = _sampleTime / (float)_sampleFrames * 1000f;
				_ftMin = _sampleMin * 1000f;
				_ftMax = _sampleMax * 1000f;
				_sampleFrames = 0;
				_sampleTime = 0f;
				_sampleMin = float.MaxValue;
				_sampleMax = 0f;
			}
		}
	}

	public override void Render(in GameTime time)
	{
		RenderTools();
		bool flag = DebugLines.Labels.Count > 0 || TrafficConfig.ShowDebug || InputOverlayVisible;
		if (!_open && !flag && (_toast == null || _time > _toastUntil))
		{
			return;
		}
		FontAtlas fontAtlas = Font();
		if (fontAtlas == null || fontAtlas.Material == null || _r.Width <= 0 || _r.Height <= 0)
		{
			return;
		}
		float num = (float)_r.Height / 1080f;
		int num2 = Math.Max(10, (int)MathF.Round(12f * num));
		float num3 = (float)num2 / Math.Max(1f, fontAtlas.LineHeight);
		float num4 = (float)fontAtlas.LineHeight * num3;
		float num5 = MathF.Max(num4 * 1.35f, (float)num2 * 1.25f);
		float cw = (float)fontAtlas.CellW * num3;
		_v.Clear();
		_i.Clear();
		RenderWorldDebug(fontAtlas, cw, num4, num5, num);
		if (InputOverlayVisible && !_open) RenderInputOverlay(fontAtlas, cw, num4, num5, num);
		if (!_open)
		{
			if (_toast != null && !(_time > _toastUntil))
			{
				float num6 = MathF.Min((float)_r.Width - 40f * num, TextWidth(fontAtlas, _toast, num3) + 20f);
				float num7 = 20f * num;
				float num8 = (float)_r.Height - num5 - 28f * num;
				Quad(num7, num8, num7 + num6, num8 + num5 + 8f, 0f, 0f, 0f, 0f, CBack);
				Text(fontAtlas, num7 + 8f, num8 + 4f, _toast, CText, num7 + num6 - 8f, cw, num4);
			}
		}
		else
		{
			List<Page> stack = _stack;
			Page page = stack[stack.Count - 1];
			int num9 = Math.Max(5, (int)MathF.Floor(((float)_r.Height * 0.8f - num5 * 3f) / num5));
			if (page.Sel < page.Top)
			{
				page.Top = page.Sel;
			}
			if (page.Sel >= page.Top + num9)
			{
				page.Top = page.Sel - num9 + 1;
			}
			page.Top = Math.Clamp(page.Top, 0, Math.Max(0, _items.Count - num9));
			int num10 = Math.Min(num9, _items.Count - page.Top);
			float num11 = MathF.Min((float)_r.Width - 40f * num, 1000f * num);
			float num12 = num5 * (float)(num10 + 3) + 12f;
			_slideX = MathF.Max(0f, (float)_r.Width - num11 - 40f * num);
			_slideY = MathF.Max(0f, (float)_r.Height - num12 - 40f * num - num5 - 12f * num);
			float num13 = 20f * num + _panelNX * _slideX;
			float num14 = 20f * num + _panelNY * _slideY;
			_panelRx = num13;
			_panelRy = num14;
			_panelW = num11;
			_panelH = num12;
			_panelHeaderH = num5 * 1.4f + 4f;
			Quad(num13, num14, num13 + num11, num14 + num12, 0f, 0f, 0f, 0f, CBack);
			Quad(num13, num14, num13 + num11, num14 + _panelHeaderH, 0f, 0f, 0f, 0f, _dragging ? CSelect : CPanel);
			Quad(num13, num14 + _panelHeaderH, num13 + num11, num14 + _panelHeaderH + 1f, 0f, 0f, 0f, 0f, CLine);
			Quad(num13, num14, num13 + num11, num14 + 1f, 0f, 0f, 0f, 0f, CLine);
			Quad(num13, num14 + num12 - 1f, num13 + num11, num14 + num12, 0f, 0f, 0f, 0f, CLine);
			Quad(num13, num14, num13 + 1f, num14 + num12, 0f, 0f, 0f, 0f, CLine);
			Quad(num13 + num11 - 1f, num14, num13 + num11, num14 + num12, 0f, 0f, 0f, 0f, CLine);
			float x = num13 + 10f;
			float num15 = num14 + 6f;
			float num16 = num13 + num11 * 0.55f;
			StringBuilder stringBuilder = new StringBuilder("TDU2 |");
			for (int i = 0; i < _stack.Count; i++)
			{
				if (i > 0)
				{
					stringBuilder.Append(" >");
				}
				stringBuilder.Append(_stack[i].Title);
			}
			Text(fontAtlas, x, num15, stringBuilder.ToString().ToUpperInvariant(), CPath, num13 + num11 - 10f, cw, num4);
			if (_drive)
			{
				string s = "[DRIVING - " + ToggleKeyName() + " = MENU]";
				float num17 = TextWidth(fontAtlas, s, num3);
				Text(fontAtlas, num13 + num11 - 10f - num17, num15, s, CWarn, num13 + num11 - 10f, cw, num4);
			}
			num15 += num5 * 1.4f;
			for (int j = page.Top; j < page.Top + num10; j++)
			{
				Item item = _items[j];
				bool flag2 = j == page.Sel;
				if (item.Header)
				{
					Text(fontAtlas, x, num15, ((item.Value != null) ? "" : "") + item.Label, CHeader, num16 - 2f, cw, num4);
					if (item.Value != null)
					{
						Text(fontAtlas, num16, num15, Safe(item.Value), item.Placeholder ? CTextDim : CValue, num13 + num11 - 10f, cw, num4);
					}
				}
				else
				{
					bool flag3 = flag2 && !_drive;
					if (flag3)
					{
						Quad(num13 + 3f, num15 - 2f, num13 + num11 - 3f, num15 + num5 - 1f, 0f, 0f, 0f, 0f, CSelect);
					}
					Text(fontAtlas, x, num15, (flag3 ? ">" : "") + item.Label, flag3 ? CAccent : CText, num16 - 2f, cw, num4);
					if (item.Value != null)
					{
						uint col = (item.Placeholder ? CTextDim : (flag2 ? CAccent : CValue));
						Text(fontAtlas, num16, num15, Safe(item.Value), col, num13 + num11 - 10f, cw, num4);
					}
				}
				num15 += num5;
			}
			string s2 = "F1 close | Up/Down entry | Left/Right value | Enter open | Del reset | " + ToggleKeyName() + " control | drag title / Ctrl+arrows move | Ctrl+Home reset";
			Text(fontAtlas, num13 + 10f, num15 + 2f, s2, CTextDim, num13 + num11 - 10f, cw, num4);
			if (_toast != null && _time <= _toastUntil)
			{
				float num18 = MathF.Min(num11, TextWidth(fontAtlas, _toast, num3) + 20f);
				float num19 = num14 + num12 + 6f * num;
				Quad(num13, num19, num13 + num18, num19 + num5 + 8f, 0f, 0f, 0f, 0f, CBack);
				Text(fontAtlas, num13 + 8f, num19 + 4f, _toast, CAccent, num13 + num18 - 8f, cw, num4);
			}
		}
		if (_i.Count > 0)
		{
			_r.DrawOverlayTop(CollectionsMarshal.AsSpan(_v), CollectionsMarshal.AsSpan(_i), fontAtlas.Material);
		}
	}

	/// <summary>
	/// Port von Unity TDU2InputOverlay: Ergebnis nach Totzone/Kurve, alle Rohachsen, gedrückte Knöpfe und
	/// Vibration als Balken. Wird angezeigt, solange das DevMenü geschlossen ist (wie im Original).
	/// </summary>
	private void RenderInputOverlay(FontAtlas f, float cw, float ch, float lh, float k)
	{
		// Unity TDU2InputOverlay nutzt 14 px bei 1080p, mindestens 0,6-fach, und 1,3-fache Zeilenhöhe.
		k = MathF.Max(0.6f, k);
		float fontPx = 14f * k;
		float fontScale = fontPx / Math.Max(1f, f.LineHeight);
		cw = f.CellW * fontScale;
		ch = fontPx;
		lh = fontPx * 1.3f;
		float pad = 8f * k, barW = 150f * k, barH = lh * 0.6f;
		float w = 330f * k;
		int count = 7 + TDU2InputDevices.AxisCount + 2 + 5 + 2;   // low, high, impact, modern channels (2 lines) + wheel + device
		float h = pad * 2f + lh * count;
		float x0 = _r.Width - w - 16f * k;
		float y0 = 90f * k;
		float x = x0 + pad, y = y0 + pad, bx = x0 + w - pad - barW;
		float maxX = x0 + w - pad;
		Quad(x0, y0, x0 + w, y0 + h, 0f, 0f, 0f, 0f, COverlayBack);

		string dev = TDU2InputDevices.HasDevice
			? TDU2InputDevices.DeviceName + " (J" + TDU2InputDevices.DeviceJoy + ") " + (TDU2InputDevices.IsWheel ? "Wheel" : "Pad")
			: "no device";
		Text(f, x, y, dev, CDevice, maxX, cw, ch); y += lh;

		float steer = TDU2InputDevices.Steer;
		Text(f, x, y, "Steering " + steer.ToString("+0.00;-0.00", Inv), CText, bx - 4f, cw, ch);
		InputCenter(bx, y, barW, barH, steer, CBarSteer); y += lh;
		Text(f, x, y, "Throttle " + TDU2InputDevices.Throttle.ToString("0.00", Inv), CText, bx - 4f, cw, ch);
		InputBar(bx, y, barW, barH, TDU2InputDevices.Throttle, CBarGas); y += lh;
		Text(f, x, y, "Brake " + TDU2InputDevices.Brake.ToString("0.00", Inv), CText, bx - 4f, cw, ch);
		InputBar(bx, y, barW, barH, TDU2InputDevices.Brake, CBarBrake); y += lh;
		Text(f, x, y, "Clutch " + TDU2InputDevices.Clutch.ToString("0.00", Inv), CText, bx - 4f, cw, ch);
		InputBar(bx, y, barW, barH, TDU2InputDevices.Clutch, CBarClutch); y += lh;
		int g = TDU2InputDevices.HGear;
		Text(f, x, y, "H-gear: " + (g < 0 ? "R" : g == 0 ? "N / -" : g.ToString(Inv)) +
			(TDU2InputDevices.IsWheel ? "   gain " + TDU2InputDevices.WheelSteerGain.ToString("0.00", Inv) : ""), CText, maxX, cw, ch); y += lh;

		Text(f, x, y, "Raw axes J" + TDU2InputDevices.DeviceJoy, CTextDim, maxX, cw, ch); y += lh;
		for (int i = 1; i <= TDU2InputDevices.AxisCount; i++)
		{
			float v = TDU2InputDevices.RawAxis(i);
			Text(f, x, y, "Axis " + i.ToString(Inv).PadLeft(2) + "  " +
				v.ToString("+0.00;-0.00", Inv) + (TDU2InputDevices.IsHatAxis(i) ? "  (hat)" : ""), CText, bx - 4f, cw, ch);
			InputCenter(bx, y, barW, barH, v, MathF.Abs(v) > 0.05f ? CBarAxis : CBarDim); y += lh;
		}

		var sb = new StringBuilder("Buttons: ");
		int nb = 0;
		for (int j = 1; j <= Math.Min(8, Joysticks.MaxJoysticks); j++)
			for (int b = 0; b < Joysticks.MaxButtons; b++)
				if (Joysticks.Button(j, b))
				{
					if (nb++ > 0) sb.Append(' ');
					sb.Append('J').Append(j).Append(':').Append(b);
				}
		if (nb == 0) sb.Append('-');
		Text(f, x, y, sb.ToString(), CTextDim, maxX, cw, ch); y += lh;
		string shift = TDU2InputDevices.ShifterJoy > 0 ? TDU2InputDevices.ShifterName + " (J" + TDU2InputDevices.ShifterJoy + ")" : "-";
		Text(f, x, y, "Shifter: " + shift, CTextDim, maxX, cw, ch); y += lh;

		float low = TDU2ForceFeedback.LastPadLow;
		Text(f, x, y, "Low vibration " + low.ToString("0.00", Inv), CText, bx - 4f, cw, ch);
		InputBar(bx, y, barW, barH, low, CBarVib); y += lh;
		float high = TDU2ForceFeedback.LastPadHigh;
		Text(f, x, y, "High vibration " + high.ToString("0.00", Inv), CText, bx - 4f, cw, ch);
		InputBar(bx, y, barW, barH, high, CBarVib); y += lh;
		Text(f, x, y, "Impact " + TDU2ForceFeedback.Impact.ToString("0.00", Inv) + "   " +
			(TDU2ForceFeedback.LastOffPaved ? "Dirt / off-road" : "Asphalt"), CTextDim, maxX, cw, ch); y += lh;
		Text(f, x, y, "Slip " + TDU2ForceFeedback.LastSlip.ToString("0.00", Inv) +
			"  ABS " + TDU2ForceFeedback.LastAbs.ToString("0.00", Inv) +
			"  Bump " + TDU2ForceFeedback.LastBump.ToString("0.00", Inv), CTextDim, maxX, cw, ch); y += lh;
		Text(f, x, y, "Understeer " + TDU2ForceFeedback.LastUndersteer.ToString("0.00", Inv) +
			"  Spin " + TDU2ForceFeedback.LastWheelspin.ToString("0.00", Inv) +
			"  Lock " + TDU2ForceFeedback.LastLock.ToString("0.00", Inv) +
			"  Surf " + TDU2ForceFeedback.LastRoughWheels.ToString(Inv), CTextDim, maxX, cw, ch); y += lh;
		int wheelRumble = Math.Clamp(TDU2ForceFeedback.LastRoadRumble + TDU2ForceFeedback.LastRpmRumble, 0, 100);
		Text(f, x, y, "Wheel align " + TDU2ForceFeedback.LastWheelAlign.ToString(Inv) +
			"  rumble " + TDU2ForceFeedback.LastWheelRumble.ToString(Inv) +
			"  road " + TDU2ForceFeedback.LastRoadType.ToString(Inv), CText, bx - 4f, cw, ch);
		InputBar(bx, y, barW, barH, wheelRumble / 100f, CBarVib); y += lh;
		string ffDevice = TDU2ForceFeedback.WheelDevice;
		Text(f, x, y, "FFB device " + (string.IsNullOrEmpty(ffDevice) ? "none" : ffDevice) +
			"  mode " + TDU2InputDevices.Mode, CTextDim, maxX, cw, ch);
	}

	private void InputBar(float x, float y, float w, float h, float v, uint col)
	{
		float yy = y + h * 0.35f;
		Quad(x, yy, x + w, yy + h, 0f, 0f, 0f, 0f, CBarBack);
		float q = Math.Clamp(v, 0f, 1f);
		if (q > 0f) Quad(x, yy, x + w * q, yy + h, 0f, 0f, 0f, 0f, col);
	}

	private void InputCenter(float x, float y, float w, float h, float v, uint col)
	{
		float yy = y + h * 0.35f, mid = x + w * 0.5f, len = w * 0.5f * Math.Clamp(v, -1f, 1f);
		Quad(x, yy, x + w, yy + h, 0f, 0f, 0f, 0f, CBarBack);
		if (len >= 0f) { if (len > 0f) Quad(mid, yy, mid + len, yy + h, 0f, 0f, 0f, 0f, col); }
		else Quad(mid + len, yy, mid, yy + h, 0f, 0f, 0f, 0f, col);
		Quad(mid - 1f, yy - 2f, mid + 1f, yy + h + 2f, 0f, 0f, 0f, 0f, CCenterTick);
	}

	private static string Safe(Func<string> f)
	{
		try
		{
			return f() ?? "";
		}
		catch (Exception)
		{
			return "?";
		}
	}

	private FontAtlas? Font()
	{
		if (_font == null && !_fontFailed)
		{
			_font = FontAtlas.Create(_r, "Segoe UI", 20);
			if (_font == null)
			{
				_fontFailed = true;
			}
		}
		return _font;
	}

	private float TextWidth(FontAtlas f, string s, float scale)
	{
		return f.Measure(s, scale);
	}

	private void Text(FontAtlas f, float x, float y, string s, uint col, float maxX, float cw, float ch)
	{
		float scale = ch / Math.Max(1f, f.LineHeight);
		TextProportional(f, x, y, s, col, maxX, scale);
	}

	private void TextProportional(FontAtlas f, float x, float y, string s, uint col, float maxX, float scale)
	{
		foreach (char c in s)
		{
			FontAtlas.Glyph glyph = f.GetGlyph(c);
			float num = glyph.Advance * scale;
			if (glyph.Width > 0f && glyph.Height > 0f)
			{
				float num2 = glyph.Width * scale;
				float num3 = glyph.Height * scale;
				if (x + num2 > maxX)
				{
					break;
				}
				Quad(x, y, x + num2, y + num3, glyph.U0, glyph.V0, glyph.U1, glyph.V1, col);
			}
			x += num;
			if (x >= maxX)
			{
				break;
			}
		}
	}

	private void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint col)
	{
		FontAtlas font = _font;
		if (u0 == 0f && v0 == 0f && u1 == 0f && v1 == 0f)
		{
			(u0, v0, u1, v1) = font.WhiteUv();
		}
		uint count = (uint)_v.Count;
		_v.Add(new ObjectVertex
		{
			Position = new Vector3(x0, y0, 0f),
			Normal = Vector3.UnitZ,
			Color = col,
			Uv0 = new System.Numerics.Vector2(u0, v0)
		});
		_v.Add(new ObjectVertex
		{
			Position = new Vector3(x1, y0, 0f),
			Normal = Vector3.UnitZ,
			Color = col,
			Uv0 = new System.Numerics.Vector2(u1, v0)
		});
		_v.Add(new ObjectVertex
		{
			Position = new Vector3(x1, y1, 0f),
			Normal = Vector3.UnitZ,
			Color = col,
			Uv0 = new System.Numerics.Vector2(u1, v1)
		});
		_v.Add(new ObjectVertex
		{
			Position = new Vector3(x0, y1, 0f),
			Normal = Vector3.UnitZ,
			Color = col,
			Uv0 = new System.Numerics.Vector2(u0, v1)
		});
		_i.Add(count);
		_i.Add(count + 1);
		_i.Add(count + 2);
		_i.Add(count);
		_i.Add(count + 2);
		_i.Add(count + 3);
	}

	private List<Item> TravelItems()
	{
		List<Item> list = new List<Item> { Head("ISLAND TRAVEL") };
		list.Add(Info("Travel", () => (!IslandTravel.Pending) ? "-" : ("to " + IslandTravel.DisplayName(IslandTravel.Target) + " …")));
		(string, string, string)[] islands = Islands;
		for (int i = 0; i < islands.Length; i++)
		{
			(string, string, string) tuple = islands[i];
			string item = tuple.Item1;
			string item2 = tuple.Item2;
			string item3 = tuple.Item3;
			string isl = item;
			string arr = item3;
			list.Add(new Item
			{
				Label = "Teleport -> " + item2,
				Value = () => (!string.Equals(Streamer?.Island, isl, StringComparison.OrdinalIgnoreCase)) ? ((!IslandTravel.CanTravelTo(isl, GameData.BnkDir)) ? ("no Bnk/islands/" + isl) : "[Enter]") : "<- here",
				Activate = delegate
				{
					Travel(isl, arr);
				}
			});
		}
		return list;
	}

	private void Travel(string island, string arrival)
	{
		if (IslandTravel.Pending)
		{
			Toast("Island travel already running");
			return;
		}
		if (WorldWeather.Inside)
		{
			Toast("Leave the interior first");
			return;
		}
		if (string.Equals(Streamer?.Island, island, StringComparison.OrdinalIgnoreCase))
		{
			Toast("Already on " + IslandTravel.DisplayName(island));
			return;
		}
		if (!IslandTravel.CanTravelTo(island, GameData.BnkDir))
		{
			Toast("Island folder missing: Bnk/islands/" + island);
			return;
		}
		IslandTravel.Go(island, flight: true, Mgr?.CurrentCar?.FileName, arrival);
		Toast("Flight to " + IslandTravel.DisplayName(island));
		Close();
	}

	private static void SetCfg(AvatarConfig c)
	{
		AvatarConfigs.SetLocal(c);
	}

	private static Item LightFloat(string label, Func<float> get, Action<float> set, float step, float min, float max, float def)
	{
		Action<float> set2 = set;
		return Float(label, get, delegate(float v)
		{
			set2(v);
			AvatarLight.Changed();
		}, step, min, max, def, "0.00");
	}

	private static float Gray(System.Numerics.Vector4 c)
	{
		return c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;
	}

	private List<Item> AvatarMenu()
	{
		List<Item> list = new List<Item>();
		if (!AvatarLibrary.Ready)
		{
			list.Add(Info("Avatar data not loaded yet"));
			return list;
		}
		list.Add(Head("OWN AVATAR"));
		list.Add(Table("Model", SexNames, () => Cfg.Sex, delegate(int v)
		{
			AvatarConfig avatarConfig2 = AvatarConfig.Default(v);
			avatarConfig2.Ethnic = Cfg.Ethnic;
			SetCfg(avatarConfig2);
			Toast("Model: " + SexNames[v]);
		}, 0));
		list.Add(Table("Ethnicity", EthNames, () => Cfg.Ethnic, delegate(int v)
		{
			AvatarConfig cfg = Cfg;
			cfg.Ethnic = v;
			SetCfg(cfg);
		}, 0));
		list.Add(Menu("Clothes", ClothesMenu));
		list.Add(Action_("Default look", delegate
		{
			AvatarConfig avatarConfig = AvatarConfig.Default(Cfg.Sex);
			avatarConfig.Ethnic = Cfg.Ethnic;
			SetCfg(avatarConfig);
			Toast("Default look");
		}));
		list.Add(Info("Encoded", delegate
		{
			string text = AvatarConfigs.LocalCode ?? "-";
			return (text.Length <= 60) ? text : (text.Substring(0, 60) + "…");
		}));
		list.Add(Head("DISPLAY"));
		list.Add(Bool("Show drivers", () => CarAvatar.ShowDrivers, delegate(bool v)
		{
			CarAvatar.ShowDrivers = v;
		}, def: true));
		list.Add(Bool("Show passengers", () => CarAvatar.ShowPassengers, delegate(bool v)
		{
			CarAvatar.ShowPassengers = v;
		}, def: true));
		list.Add(Bool("NPCs in interiors", () => InteriorNpcs.Show, delegate(bool v)
		{
			InteriorNpcs.Show = v;
		}, def: true));
		list.Add(Bool("Hide head in cockpit view", () => CarAvatar.All.Count == 0 || CarAvatar.All[0].HideHeadInCockpit, delegate(bool v)
		{
			foreach (CarAvatar item in CarAvatar.All)
			{
				item.HideHeadInCockpit = v;
			}
		}, def: true));
		list.Add(Head("SHADER"));
		list.Add(LightFloat("Skin ramp + boost", () => AvatarLight.RampAdd, delegate(float v)
		{
			AvatarLight.RampAdd = v;
		}, 0.05f, -1.5f, 2f, 0f));
		list.Add(LightFloat("Skin ramp row", () => AvatarLight.RampRow, delegate(float v)
		{
			AvatarLight.RampRow = v;
		}, 0.05f, 0f, 1f, 0f));
		list.Add(LightFloat("Ambient on lit side", () => AvatarLight.AddMix, delegate(float v)
		{
			AvatarLight.AddMix = v;
		}, 0.05f, 0f, 1f, 0f));
		list.Add(LightFloat("Extra ambient light", () => AvatarLight.AmbAdd, delegate(float v)
		{
			AvatarLight.AmbAdd = v;
		}, 0.02f, -0.5f, 1f, 0f));
		list.Add(LightFloat("Extra hair sheen", () => AvatarLight.HairSheen, delegate(float v)
		{
			AvatarLight.HairSheen = v;
		}, 0.05f, -0.33f, 1f, 0f));
		list.Add(LightFloat("Indoor: ceiling light", () => Gray(AvatarLight.IndoorLight), delegate(float v)
		{
			AvatarLight.IndoorLight = new System.Numerics.Vector4(v, v * 0.94f, v * 0.86f, 1f);
		}, 0.02f, 0f, 2f, 0.47f));
		list.Add(LightFloat("Indoor: ambient top", () => Gray(AvatarLight.IndoorAmbTop), delegate(float v)
		{
			AvatarLight.IndoorAmbTop = new System.Numerics.Vector4(v, v * 0.96f, v * 0.9f, 1f);
		}, 0.02f, 0f, 2f, 0.44f));
		list.Add(LightFloat("Indoor: ambient bottom", () => Gray(AvatarLight.IndoorAmbBottom), delegate(float v)
		{
			AvatarLight.IndoorAmbBottom = new System.Numerics.Vector4(v, v * 0.95f, v * 0.9f, 1f);
		}, 0.02f, 0f, 2f, 0.19f));
		list.Add(Head("ANIMATION"));
		if (_clipNames == null)
		{
			_clipNames = AvatarLibrary.ClipNames();
		}
		List<string> clips = _clipNames;
		list.Add(new Item
		{
			Label = "Clip",
			Value = () => (!string.IsNullOrEmpty(CarAvatar.DebugClip)) ? CarAvatar.DebugClip : "Seated pose",
			Change = delegate(int d)
			{
				int num = clips.IndexOf(CarAvatar.DebugClip ?? "");
				int num2 = clips.Count + 1;
				num = ((num + 1 + d) % num2 + num2) % num2 - 1;
				CarAvatar.DebugClip = ((num < 0) ? null : clips[num]);
			},
			Reset = delegate
			{
				CarAvatar.DebugClip = null;
			}
		});
		list.Add(Float("Playback speed", () => CarAvatar.DebugSpeed, delegate(float v)
		{
			CarAvatar.DebugSpeed = v;
		}, 0.1f, 0f, 4f, 1f, "0.0", "x"));
		list.Add(Bool("IK hands/feet", () => CarAvatar.DebugIK, delegate(bool v)
		{
			CarAvatar.DebugIK = v;
		}, def: true));
		list.Add(Head("INFO / ACTIONS"));
		list.Add(Info("Avatars in cars / NPCs", () => CarAvatar.All.Count + " / " + InteriorNpcs.Npcs.Count));
		list.Add(Info("Clips in avatarsdriver.bnk", () => clips.Count.ToString(Inv)));
		list.Add(Action_("Rebuild all avatars", delegate
		{
			CarAvatar[] array = CarAvatar.All.ToArray();
			foreach (CarAvatar carAvatar in array)
			{
				string s = carAvatar.ConfigCode?.Invoke();
				carAvatar.Rebuild(AvatarConfig.Decode(s) ?? carAvatar.Avatar?.Config ?? Cfg);
			}
			Toast("Avatars rebuilt");
		}));
		return list;
	}

	private static List<ClotheInfo> PartItems(ClothePart part, int sex)
	{
		List<ClotheInfo> list = new List<ClotheInfo>();
		foreach (ClotheInfo allClothe in AvatarLibrary.AllClothes)
		{
			if (allClothe.Part == part && allClothe.Valid && !allClothe.IsNothing && ((sex == 1) ? allClothe.ForWoman : allClothe.ForMan) && AvatarLibrary.HasBank(allClothe))
			{
				list.Add(allClothe);
			}
		}
		list.Sort((ClotheInfo a, ClotheInfo b) => string.CompareOrdinal(a.File, b.File));
		return list;
	}

	private List<Item> ClothesMenu()
	{
		List<Item> list2 = new List<Item> { Head("CLOTHES") };
		int sex = Cfg.Sex;
		(ClothePart, string)[] avatarParts = AvatarParts;
		for (int i = 0; i < avatarParts.Length; i++)
		{
			(ClothePart, string) tuple = avatarParts[i];
			ClothePart item = tuple.Item1;
			string item2 = tuple.Item2;
			ClothePart p = item;
			if ((p == ClothePart.Dress && sex != 1) || (p == ClothePart.Beard && sex == 1))
			{
				continue;
			}
			List<ClotheInfo> list = PartItems(p, sex);
			if (list.Count == 0)
			{
				continue;
			}
			list2.Add(new Item
			{
				Label = item2 + "" + list.Count + "",
				Value = () => Cfg.Worn(p)?.File ?? "-",
				Change = delegate(int d)
				{
					AvatarConfig cfg4 = Cfg;
					ClotheInfo clotheInfo4 = cfg4.Worn(p);
					int num2 = ((clotheInfo4 != null) ? list.IndexOf(clotheInfo4) : (-1));
					int num3 = list.Count + 1;
					num2 = ((num2 + 1 + d) % num3 + num3) % num3 - 1;
					cfg4.Remove(p);
					if (num2 >= 0)
					{
						cfg4.Set(list[num2], 0);
					}
					SetCfg(cfg4);
				},
				Reset = delegate
				{
					AvatarConfig cfg3 = Cfg;
					cfg3.Remove(p);
					SetCfg(cfg3);
				}
			});
			list2.Add(new Item
			{
				Label = " Colour",
				Value = delegate
				{
					ClotheInfo clotheInfo3 = Cfg.Worn(p);
					return (clotheInfo3 == null) ? "-" : (Cfg.ColorOf(clotheInfo3.Hash) + 1 + " / " + Math.Max(1, clotheInfo3.NbColors));
				},
				Change = delegate(int d)
				{
					AvatarConfig cfg2 = Cfg;
					ClotheInfo clotheInfo2 = cfg2.Worn(p);
					if (clotheInfo2 != null)
					{
						int num = Math.Max(1, clotheInfo2.NbColors);
						int color = ((cfg2.ColorOf(clotheInfo2.Hash) + Math.Sign(d)) % num + num) % num;
						cfg2.Set(clotheInfo2, color);
						SetCfg(cfg2);
					}
				},
				Reset = delegate
				{
					AvatarConfig cfg = Cfg;
					ClotheInfo clotheInfo = cfg.Worn(p);
					if (clotheInfo != null)
					{
						cfg.Set(clotheInfo, 0);
						SetCfg(cfg);
					}
				}
			});
		}
		return list2;
	}

	private List<Item> GpsMenu()
	{
		Gps gps = Gps.Instance;
		List<Item> list = new List<Item> { Head("GPS") };
		if (gps == null)
		{
			list.Add(Info("GPS not active yet"));
		}
		else
		{
			list.Add(Info("Target", () => (!gps.HasTarget) ? "none" : (Num(gps.TargetGame.X) + " / " + Num(gps.TargetGame.Y) + " / " + Num(gps.TargetGame.Z))));
			list.Add(Info("Remaining", () => (!gps.HasTarget || !(gps.Remaining >= 0f)) ? "-" : (Num(gps.Remaining) + " m " + gps.Route.Count + " points")));
			list.Add(Action_("Clear target", delegate
			{
				gps.ClearTarget();
				Toast("GPS target cleared");
			}));
		}
		list.Add(Float("Arrived below", () => TDU2GpsSettings.ArriveDistance, delegate(float v)
		{
			TDU2GpsSettings.ArriveDistance = v;
		}, 5f, 5f, 200f, 50f, "0", " m"));
		list.Add(Float("Path width", () => TDU2GpsSettings.PathWidth, delegate(float v)
		{
			TDU2GpsSettings.PathWidth = v;
		}, 0.002f, 0.004f, 0.08f, 0.02f, "0.000"));
		list.Add(Float("Ring spread", () => TDU2GpsSettings.RingSpread, delegate(float v)
		{
			TDU2GpsSettings.RingSpread = v;
		}, 5f, 5f, 180f, 30f, "0", "°"));
		list.Add(Bool("Miles", () => TDU2GpsSettings.Miles, delegate(bool v)
		{
			TDU2GpsSettings.Miles = v;
		}, def: false));
		list.Add(Bool("Distance voices", () => TDU2GpsSettings.DistanceVoices, delegate(bool v)
		{
			TDU2GpsSettings.DistanceVoices = v;
		}, def: true));
		list.Add(Head("VOICE"));
		list.Add(Bool("Voice", () => TDU2GpsSettings.Voice, delegate(bool v)
		{
			TDU2GpsSettings.Voice = v;
		}, def: true));
		list.Add(Float("Volume", () => TDU2GpsSettings.VoiceVolume, delegate(float v)
		{
			TDU2GpsSettings.VoiceVolume = v;
		}, 0.05f, 0f, 1f, 0.85f, "0.00"));
		string[] langs = new string[7] { "", "ge", "us", "fr", "it", "sp", "ja" };
		string[] names = new string[7] { "System language", "German", "English", "French", "Italian", "Spanish", "Japanese" };
		list.Add(Table("Language", names, () => Math.Max(0, Array.IndexOf(langs, TDU2GpsSettings.VoiceLanguage ?? "")), delegate(int v)
		{
			TDU2GpsSettings.VoiceLanguage = langs[v];
		}, 0));
		int num = Math.Max(17, gps?.VoiceCount ?? 0);
		list.Add(Int("Target set", () => TDU2GpsSettings.VoiceTargetSet, delegate(int v)
		{
			TDU2GpsSettings.VoiceTargetSet = v;
		}, 0, num, 0));
		list.Add(Int("Arrived", () => TDU2GpsSettings.VoiceArrived, delegate(int v)
		{
			TDU2GpsSettings.VoiceArrived = v;
		}, 0, num, 16));
		list.Add(Int("Left soon", () => TDU2GpsSettings.VoiceTurnLeftSoon, delegate(int v)
		{
			TDU2GpsSettings.VoiceTurnLeftSoon = v;
		}, 0, num, 4));
		list.Add(Int("Right soon", () => TDU2GpsSettings.VoiceTurnRightSoon, delegate(int v)
		{
			TDU2GpsSettings.VoiceTurnRightSoon = v;
		}, 0, num, 2));
		list.Add(Int("Left now", () => TDU2GpsSettings.VoiceTurnLeftNow, delegate(int v)
		{
			TDU2GpsSettings.VoiceTurnLeftNow = v;
		}, 0, num, 5));
		list.Add(Int("Right now", () => TDU2GpsSettings.VoiceTurnRightNow, delegate(int v)
		{
			TDU2GpsSettings.VoiceTurnRightNow = v;
		}, 0, num, 3));
		list.Add(Int("U-turn", () => TDU2GpsSettings.VoiceUTurn, delegate(int v)
		{
			TDU2GpsSettings.VoiceUTurn = v;
		}, 0, num, 1));
		if (gps != null)
		{
			list.Add(Head("LISTEN TO VOICES"));
			for (int j = 1; j <= num; j++)
			{
				int i = j;
				list.Add(Action_("VFGPSVoice" + i.ToString("000", Inv) + " play", delegate
				{
					bool voice = TDU2GpsSettings.Voice;
					TDU2GpsSettings.Voice = true;
					gps.Voice(i);
					TDU2GpsSettings.Voice = voice;
					Toast("GPS voice " + i);
				}));
			}
		}
		list.Add(Head("BOOKMARKS"));
		list.Add(Info("Saved", () => TDU2Bookmarks.All.Count + " / " + TDU2Bookmarks.Max + (TDU2Session.LoggedIn ? "" : "")));
		foreach (TDU2Bookmarks.Mark item in new List<TDU2Bookmarks.Mark>(TDU2Bookmarks.All))
		{
			TDU2Bookmarks.Mark bm = item;
			list.Add(new Item
			{
				Label = "Bookmark " + bm.Id + "" + IslandTravel.DisplayName(bm.Island) + "" + Num(bm.Pos.X) + " / " + Num(bm.Pos.Z) + "",
				Value = () => "Enter = GPS target · Delete = remove",
				Activate = delegate
				{
					if (!string.Equals(bm.Island, Streamer?.Island, StringComparison.OrdinalIgnoreCase))
					{
						Toast("Bookmark is on " + IslandTravel.DisplayName(bm.Island));
					}
					else if (Gps.Instance == null)
					{
						Toast("GPS not active");
					}
					else
					{
						Gps.Instance.SetTarget(bm.Pos);
						Toast("GPS target: bookmark " + bm.Id);
					}
				},
				Reset = delegate
				{
					TDU2Bookmarks.Remove(bm.Id);
					Toast("Bookmark deleted");
					Rebuild();
				}
			});
		}
		list.Add(Info("Favourite spots", () => TDU2Session.Save?.favoriteSpots?.Count.ToString(Inv) ?? "local"));
		return list;
	}

	private List<Item> NetworkMenu()
	{
		return new List<Item>
		{
			Head("STATUS"),
			Info("Logged in", () => (!TDU2Session.LoggedIn) ? "NO" : ("YES " + TDU2Session.Nick + ", Id " + TDU2Session.PlayerId + "")),
			Info("Session", () => (!TDU2Session.Offline) ? "server" : "offline profile"),
			Info("Online", () => (!TDU2GameData.Online) ? "NO" : "YES"),
			Info("Players online", () => TDU2GameData.PlayerCount.ToString(Inv)),
			Info("Lobby", () => TDU2GameData.Lobby + "" + TDU2GameData.LobbyCount + " / " + TDU2GameData.LobbyMax + ""),
			Info("Own island", () => IslandTravel.DisplayName(Streamer?.Island) + "" + TDU2GameData.LocalIsland + ""),
			Info("Server time", () => DateTimeOffset.FromUnixTimeMilliseconds((long)(TDU2Session.ServerSeconds * 1000.0)).ToLocalTime().ToString("HH:mm:ss", Inv)),
			Info("Saving", () => (!TDU2Session.Saving) ? "-" : "running"),
			Head("NETWORK TRAFFIC"),
			Info("Remote player cars", delegate
			{
				int num = 0;
				foreach (RemoteCars.Remote item in RemoteCars.All)
				{
					num++;
				}
				return num.ToString(Inv);
			}),
			Head("SETTINGS"),
			Bool("Player join/leave messages", () => TDU2GameData.ShowPlayerEvents, delegate(bool v)
			{
				TDU2GameData.ShowPlayerEvents = v;
			}, def: true),
			Float("Status message duration", () => TDU2GameData.StatusSeconds, delegate(float v)
			{
				TDU2GameData.StatusSeconds = v;
			}, 0.5f, 0.5f, 15f, 3f, "0.0", " s"),
			PhBool("Hide other players", def: false),
			PhBool("Names above players", def: true),
			PhBool("Collision with players", def: true),
			PhBool("Other players' engine sounds", def: true),
			PhFloat("Send rate", 1f, 2f, 60f, 20f, "0", " /s"),
			PhFloat("Interpolation", 0.01f, 0f, 1f, 0.12f, "0.00", " s"),
			Head("ACTIONS"),
			Action_("Save game now", delegate
			{
				if (!TDU2Session.LoggedIn)
				{
					Toast("Not logged in");
				}
				else
				{
					TDU2Session.SaveAsync();
					Toast("Saving game");
				}
			})
		};
	}

	private List<Item> PlayersMenu()
	{
		List<Item> list = new List<Item> { Head("PLAYERS") };
		List<PlayerInfo> list2 = new List<PlayerInfo>(TDU2GameData.Players.Values);
		list2.Sort((PlayerInfo a, PlayerInfo b) => string.Compare(a.Nick, b.Nick, StringComparison.OrdinalIgnoreCase));
		foreach (PlayerInfo item in list2)
		{
			PlayerInfo pl = item;
			list.Add(Info((pl.Nick.Length > 0) ? pl.Nick : ("#" + pl.Id), () => (pl.Online ? "online" : "offline") + " · " + ((pl.Island == 2) ? "Hawaii" : ((pl.Island == 1) ? "Ibiza" : "?")) + " · $ " + pl.Money.ToString("N0", Inv) + " · cars " + pl.NbCars + ((pl.CarName.Length > 0) ? (" · " + pl.CarName) : "")));
		}
		if (list2.Count == 0)
		{
			list.Add(Info("no other players"));
		}
		return list;
	}

	private List<Item> NetworkTrafficMenu()
	{
		TDU2PeerMesh mesh = Multiplayer.Mesh;
		TDU2NetClient tc = TDU2Session.Client;
		List<Item> list = new List<Item> { Head("NETWORK TRAFFIC") };
		list.Add(Head("SERVER TCP"));
		list.Add(Info("Connection", () => (!TDU2Session.Offline) ? ((tc != null) ? ((!tc.Connected) ? "disconnected" : "CONNECTED") : "-") : "offline profile"));
		list.Add(Info("Host / port", () => (tc == null) ? "-" : (tc.Host + ":" + tc.Port.ToString(Inv))));
		list.Add(Info("Requests / responses", () => (tc == null) ? "-" : (tc.Requests.ToString(Inv) + " / " + tc.Responses.ToString(Inv))));
		list.Add(Info("Bytes in / out", () => (tc == null) ? "-" : (B(tc.BytesIn) + " / " + B(tc.BytesOut))));
		list.Add(Info("Request time", () => (tc == null) ? "-" : Ms(tc.LastRttMs)));
		list.Add(Info("Connect generations / failures", () => (tc == null) ? "-" : (tc.Generation.ToString(Inv) + " / " + tc.Failures.ToString(Inv))));
		list.Add(Info("Last error", () => (tc == null || string.IsNullOrEmpty(tc.LastError)) ? "-" : tc.LastError));
		list.Add(Head("SERVER UDP"));
		list.Add(Info("Socket", () => (!Multiplayer.ServerSocketUp) ? "down" : ("UP " + Multiplayer.LocalEndpoint)));
		list.Add(Info("Server endpoint", () => Multiplayer.ServerEndpoint));
		list.Add(Info("Packets in / out", () => Multiplayer.ServerPacketsIn.ToString(Inv) + " / " + Multiplayer.ServerPacketsOut.ToString(Inv)));
		list.Add(Info("Bytes in / out", () => B(Multiplayer.ServerBytesIn) + " / " + B(Multiplayer.ServerBytesOut)));
		list.Add(Info("RTT last / smooth", () => Ms(Multiplayer.PingLastMs) + " / " + Ms(Multiplayer.PingMs)));
		list.Add(Info("Status poll", () => TDU2GameData.StatusIntervalSeconds.ToString("0.0", Inv) + " s · age " + ((TDU2GameData.LastStatusAge < 0f) ? "-" : (TDU2GameData.LastStatusAge.ToString("0.0", Inv) + " s")) + " · fails " + TDU2GameData.StatusFailCount.ToString(Inv)));
		list.Add(Info("Status result", () => TDU2GameData.LastStatusOk ? ("ok" + (string.IsNullOrEmpty(TDU2GameData.LastStatusError) ? "" : ("" + TDU2GameData.LastStatusError + ""))) : "-"));
		list.Add(Info("UDP send / recv errors", () => Multiplayer.UdpSendErrors.ToString(Inv) + " / " + Multiplayer.UdpRecvErrors.ToString(Inv)));
		list.Add(Head("P2P DIRECT"));
		list.Add(Info("P2P active", () => (mesh != null) ? ((!mesh.Active) ? "no" : "YES") : "-"));
		list.Add(Info("Server allows / local config", () => ((!TDU2GameData.LastStatusOk) ? "-" : (TDU2GameData.LastStatusP2PEnabled ? "yes" : "no")) + " / " + (OnlineConfig.UseP2P ? "yes" : "no")));
		list.Add(Info("Peers / direct / relay", () => (mesh != null) ? (mesh.PeerCount.ToString(Inv) + " / " + mesh.DirectCount.ToString(Inv) + " / " + mesh.RelayCount.ToString(Inv)) : "-"));
		list.Add(Info("Direct packets in / out", () => (mesh != null) ? (mesh.DirectPacketsIn.ToString(Inv) + " / " + mesh.DirectPacketsOut.ToString(Inv)) : "-"));
		list.Add(Info("Direct bytes in / out", () => (mesh != null) ? (B(mesh.DirectBytesIn) + " / " + B(mesh.DirectBytesOut)) : "-"));
		list.Add(Info("Punch packets in / out", () => (mesh != null) ? (mesh.PunchPacketsIn.ToString(Inv) + " / " + mesh.PunchPacketsOut.ToString(Inv)) : "-"));
		list.Add(Info("Direct send errors", () => (mesh != null) ? mesh.DirectSendErrors.ToString(Inv) : "-"));
		list.Add(Head("RELAY"));
		list.Add(Info("Relay peers", () => (mesh != null) ? mesh.RelayCount.ToString(Inv) : "-"));
		list.Add(Info("Relay envelopes / bytes", () => (mesh != null) ? (mesh.RelayPacketsOut.ToString(Inv) + " / " + B(mesh.RelayBytesOut)) : "-"));
		list.Add(Info("Note", () => "server UDP out also contains relay envelopes"));
		list.Add(Head("QUEUES / PACKET HANDLING"));
		list.Add(Info("Traffic inbox", () => Multiplayer.TrafficInboxCount.ToString(Inv)));
		list.Add(Info("Dispatcher accepted / dropped", () => Multiplayer.DispatchReceived.ToString(Inv) + " / " + Multiplayer.DispatchDropped.ToString(Inv)));
		list.Add(Info("Dispatcher drop rate", delegate
		{
			long num = Multiplayer.DispatchReceived + Multiplayer.DispatchDropped;
			return (num != 0L) ? ((100.0 * (double)Multiplayer.DispatchDropped / (double)num).ToString("0.0", Inv) + " %") : "-";
		}));
		list.Add(Info("Store router complete / failed / unknown", () => PacketRouter.CompletePackets.ToString(Inv) + " / " + PacketRouter.FailedPackets.ToString(Inv) + " / " + PacketRouter.UnknownPackets.ToString(Inv)));
		list.Add(Head("REMOTE PLAYER CARS"));
		List<(int id, bool loaded, string car, float age)> remotes = new List<(int, bool, string, float)>(RemoteCars.Info());
		list.Add(Info("Remote cars", () => remotes.Count.ToString(Inv)));
		foreach (var item in remotes)
		{
			(int id, bool loaded, string car, float age) rr = item;
			list.Add(Info("#" + rr.id.ToString(Inv), () => (rr.loaded ? "loaded" : "loading/none") + " · " + rr.car + " · packet " + rr.age.ToString("0.0", Inv) + " s ago" + ((mesh != null && mesh.IsDirect(rr.id)) ? " · direct" : " · relay/server")));
		}
		if (remotes.Count == 0)
		{
			list.Add(Info("no remote cars"));
		}
		list.Add(Head("PLAYERS"));
		list.Add(Info("Online", () => TDU2GameData.Players.Count.ToString(Inv) + ""));
		return list;
		static string B(long n)
		{
			if (n > 0)
			{
				if (n >= 1024)
				{
					if (n >= 1048576)
					{
						return ((double)n / 1048576.0).ToString("0.0", Inv) + " MiB";
					}
					return ((double)n / 1024.0).ToString("0.0", Inv) + " KiB";
				}
				return n.ToString(Inv) + " B";
			}
			return "0 B";
		}
		static string Ms(double ms)
		{
			if (!(ms < 0.0))
			{
				return ms.ToString("0", Inv) + " ms";
			}
			return "-";
		}
	}

	private List<Item> WrecksMenu()
	{
		Wrecks w = Wrecks.Current;
		List<Item> list = new List<Item> { Head("WRECKS / DETECTOR") };
		if (w == null)
		{
			list.Add(Info("Wrecks system not active"));
			return list;
		}
		list.Add(Info("Island", () => (!string.IsNullOrEmpty(w.Island)) ? IslandTravel.DisplayName(w.Island) : "-"));
		list.Add(Info("Found / total", () => w.FoundCount + " / " + w.Total));
		list.Add(Info("Detector", () => w.Enabled ? ((!w.DetectorUnlocked) ? ("LOCKED: level " + Wrecks.CurrentRacingLevel().ToString(Inv) + " < " + 3.ToString(Inv)) : ("unlocked " + Num(w.DetectorRange) + " m")) : "disabled"));
		list.Add(Bool("Show minimap on/off", () => w.ShowMinimapBlips, delegate(bool v)
		{
			w.ShowMinimapBlips = v;
		}, def: false));
		list.Add(Menu("Settings", WrecksSettingsMenu));
		list.Add(Menu("Found wrecks", WrecksFoundMenu));
		list.Add(Menu("All wrecks", WrecksAllMenu));
		list.Add(Head("ISLAND SWITCH"));
		(string, string, string)[] islands = Islands;
		for (int i = 0; i < islands.Length; i++)
		{
			(string, string, string) tuple = islands[i];
			string item = tuple.Item1;
			string item2 = tuple.Item2;
			string item3 = tuple.Item3;
			string isl = item;
			string arr = item3;
			list.Add(new Item
			{
				Label = "Travel -> " + item2,
				Value = () => (!string.Equals(w.Island, isl, StringComparison.OrdinalIgnoreCase)) ? ((!IslandTravel.CanTravelTo(isl, GameData.BnkDir)) ? ("no Bnk/islands/" + isl) : "[Enter]") : "<- here",
				Activate = delegate
				{
					Travel(isl, arr);
				}
			});
		}
		list.Add(Action_("Reload wreck data on this island", delegate
		{
			bool flag = w.ReloadCurrentIsland();
			Toast(flag ? ("Wreck data reloaded: " + w.Island) : "Reload failed");
		}));
		list.Add(Info("Note", () => "Travel closes the menu; wrecks reload for the new island automatically"));
		return list;
	}

	private List<Item> WrecksSettingsMenu()
	{
		Wrecks w = Wrecks.Current;
		List<Item> list = new List<Item> { Head("WRECKS / SETTINGS") };
		if (w == null)
		{
			list.Add(Info("Wrecks system not active"));
			return list;
		}
		list.Add(Bool("Wrecks enabled", () => w.Enabled, delegate(bool v)
		{
			w.Enabled = v;
		}, def: true));
		list.Add(Bool("Test: ignore level minimum", () => Wrecks.TestBypassLevelMinimum, delegate(bool v)
		{
			Wrecks.TestBypassLevelMinimum = v;
		}, def: true));
		list.Add(Info("Racing level / required", () => Wrecks.CurrentRacingLevel().ToString(Inv) + " / " + 3.ToString(Inv)));
		list.Add(Bool("Detailed log", () => w.DetailedLog, delegate(bool v)
		{
			w.DetailedLog = v;
		}, def: false));
		list.Add(Head("DETECTOR"));
		list.Add(Float("Detector range", () => w.DetectorRange, delegate(float v)
		{
			w.DetectorRange = v;
		}, 10f, 10f, 1000f, 250f, "0", " m"));
		list.Add(Float("Activation distance", () => w.ActivationDistance, delegate(float v)
		{
			w.ActivationDistance = v;
		}, 1f, 0.5f, 50f, 10f, "0.0", " m"));
		list.Add(Float("Bip far", () => w.BipFar, delegate(float v)
		{
			w.BipFar = v;
		}, 0.05f, 0.05f, 5f, 1.35f, "0.00", " s"));
		list.Add(Float("Bip near", () => w.BipNear, delegate(float v)
		{
			w.BipNear = v;
		}, 0.05f, 0.05f, 5f, 0.2f, "0.00", " s"));
		list.Add(Action_("Restore config from wrecks_config.cpr", delegate
		{
			w.RestoreConfig();
			Toast("Detector config restored");
		}));
		return list;
	}

	private List<Item> WrecksFoundMenu()
	{
		Wrecks current = Wrecks.Current;
		List<Item> list = new List<Item> { Head("FOUND WRECKS") };
		if (current == null)
		{
			list.Add(Info("Wrecks system not active"));
			return list;
		}
		int num = 0;
		foreach (Wrecks.Point point in current.Points)
		{
			if (point.Found)
			{
				num++;
				list.Add(WreckPointItem(current, point, teleportOnEnter: true));
			}
		}
		if (num == 0)
		{
			list.Add(Info("no wrecks found on this island"));
		}
		list.Add(Info("Hint", () => "Removed finds stay removed until you leave the wreck radius"));
		return list;
	}

	private List<Item> WrecksAllMenu()
	{
		Wrecks current = Wrecks.Current;
		List<Item> list = new List<Item> { Head("ALL WRECKS") };
		if (current == null)
		{
			list.Add(Info("Wrecks system not active"));
			return list;
		}
		foreach (Wrecks.Point point in current.Points)
		{
			list.Add(WreckPointItem(current, point, teleportOnEnter: false));
		}
		if (current.Total == 0)
		{
			list.Add(Info("no wreck data loaded for this island"));
		}
		return list;
	}

	private Item WreckPointItem(Wrecks w, Wrecks.Point p, bool teleportOnEnter)
	{
		Wrecks.Point p2 = p;
		Wrecks w2 = w;
		Vector3 pos = p2.Position;
		string coords = Num(pos.X) + " / " + Num(pos.Y) + " / " + Num(pos.Z) + " · area " + p2.Area.ToString(Inv);
		return new Item
		{
			Label = "#" + p2.Index.ToString("000", Inv) + "" + (string.IsNullOrEmpty(p2.Name) ? "" : p2.Name),
			Value = () => (p2.Found ? "FOUND" : "-") + "" + coords,
			Activate = delegate
			{
				if (teleportOnEnter)
				{
					VehicleManager mgr = Mgr;
					if (mgr == null)
					{
						Toast("No vehicle manager");
					}
					else
					{
						mgr.TeleportSafe(pos, Vector3.UnitZ);
						Toast("At wreck #" + p2.Index.ToString("000", Inv) + "" + p2.Name);
					}
				}
				else
				{
					w2.SetFound(p2.Index, !p2.Found);
					Toast("Wreck #" + p2.Index.ToString("000", Inv) + "" + (p2.Found ? "marked found" : "find removed"));
				}
			},
			Change = delegate(int d)
			{
				if (d != 0)
				{
					w2.SetFound(p2.Index, d > 0);
				}
			},
			Reset = delegate
			{
				w2.SetFound(p2.Index, found: false);
				Toast("Find removed: #" + p2.Index.ToString("000", Inv));
			}
		};
	}

	private List<Item> DiscoveryMenu()
	{
		Discovery d = Discovery.Current;
		List<Item> list = new List<Item> { Head("CRUISING / DISCOVERY") };
		if (d == null)
		{
			list.Add(Info("Discovery system not active"));
			return list;
		}
		list.Add(Info("Island", () => (!string.IsNullOrEmpty(d.Island)) ? IslandTravel.DisplayName(d.Island) : "-"));
		list.Add(Info("Area", () => (!string.IsNullOrEmpty(d.AreaName)) ? (d.AreaName + "" + d.Area.ToString(Inv) + "") : "-"));
		list.Add(Info("Roads unlocked / total", () => d.UnlockedRoadCount + " / " + d.RoadCount));
		list.Add(Info("Dealers unlocked / total", () => d.UnlockedDealerCount + " / " + d.DealerCount));
		list.Add(Info("Level", () => d.Level.ToString(Inv) + " next " + d.NextThreshold.ToString(Inv)));
		list.Add(Info("Points", () => d.RoadPoints.ToString(Inv) + " road / " + d.TotalPoints.ToString(Inv) + " total"));
		list.Add(Info("Last road", () => d.LastRoadId));
		list.Add(Menu("Roads", DiscoveryRoadsMenu));
		list.Add(Menu("Dealers", DiscoveryDealersMenu));
		list.Add(Int("Add cruising points", () => _discoveryAddPoints, delegate(int v)
		{
			_discoveryAddPoints = v;
			d.AddPoints(v);
			Toast("Cruising points " + ((v > 0) ? "+" : "") + v.ToString(Inv));
		}, -1000, 1000, 100));
		list.Add(Action_("Unlock next road", delegate
		{
			d.UnlockNext();
			Toast("Next road unlocked");
		}));
		list.Add(Action_("Unlock all roads", delegate
		{
			d.UnlockAll(currentAreaOnly: true);
			Toast("All roads of current area unlocked");
		}));
		list.Add(Action_("Unlock all roads", delegate
		{
			d.UnlockAll(currentAreaOnly: false);
			Toast("All roads unlocked");
		}));
		list.Add(Action_("Unlock all dealers", delegate
		{
			d.UnlockAllDealers();
			Toast("All dealers unlocked");
		}));
		list.Add(Action_("Refresh map colors", delegate
		{
			d.Refresh();
			Toast("Map refreshed");
		}));
		list.Add(Action_("Save now", delegate
		{
			d.SaveNow();
			Toast("Discovery saved");
		}));
		list.Add(Action_("Reload from savegame", delegate
		{
			d.Reload();
			Toast("Discovery reloaded");
		}));
		list.Add(Action_("REMOVE ALL PROGRESS", delegate
		{
			d.Clear();
			Toast("Discovery progress removed");
		}));
		return list;
	}

	private List<Item> DiscoveryRoadsMenu()
	{
		Discovery d = Discovery.Current;
		List<Item> list = new List<Item> { Head("ROADS") };
		if (d == null)
		{
			list.Add(Info("Discovery system not active"));
			return list;
		}
		string[] ids = d.RoadIds;
		_discoveryRoadIndex = Math.Clamp(_discoveryRoadIndex, 0, Math.Max(0, ids.Length - 1));
		list.Add(Int("Selected road index", () => _discoveryRoadIndex, delegate(int v)
		{
			_discoveryRoadIndex = Math.Clamp(v, 0, Math.Max(0, ids.Length - 1));
		}, 0, Math.Max(0, ids.Length - 1), 0));
		list.Add(Info("Selected id", () => (ids.Length != 0) ? ids[Math.Clamp(_discoveryRoadIndex, 0, ids.Length - 1)] : "-"));
		list.Add(Action_("Toggle selected road", delegate
		{
			if (ids.Length == 0)
			{
				Toast("No roads loaded");
			}
			else
			{
				string id2 = ids[Math.Clamp(_discoveryRoadIndex, 0, ids.Length - 1)];
				bool flag = !d.IsRoadUnlocked(id2);
				d.SetRoadUnlocked(id2, flag);
				Toast("Road " + (flag ? "unlocked" : "removed"));
			}
		}));
		list.Add(Head("FIRST " + Math.Min(40, ids.Length).ToString(Inv) + " ROADS"));
		for (int i = 0; i < ids.Length && i < 40; i++)
		{
			string id = ids[i];
			list.Add(new Item
			{
				Label = "#" + i.ToString("000", Inv) + "" + ShortRoadId(id),
				Value = () => (!d.IsRoadUnlocked(id)) ? "GRAY" : "COLORED",
				Change = delegate(int delta)
				{
					if (delta != 0)
					{
						d.SetRoadUnlocked(id, delta > 0);
					}
				},
				Reset = delegate
				{
					d.SetRoadUnlocked(id, unlocked: false);
				},
				Activate = delegate
				{
					d.SetRoadUnlocked(id, !d.IsRoadUnlocked(id));
				}
			});
		}
		return list;
	}

	private List<Item> DiscoveryDealersMenu()
	{
		Discovery d = Discovery.Current;
		List<Item> list = new List<Item> { Head("DEALERS") };
		if (d == null)
		{
			list.Add(Info("Discovery system not active"));
			return list;
		}
		string[] dealerCodes = d.DealerCodes;
		foreach (string code in dealerCodes)
		{
			list.Add(new Item
			{
				Label = "Dealer " + code,
				Value = () => (!d.IsDealerUnlocked(code)) ? "HIDDEN" : "VISIBLE",
				Change = delegate(int delta)
				{
					if (delta != 0)
					{
						d.SetDealerUnlocked(code, delta > 0);
					}
				},
				Reset = delegate
				{
					d.SetDealerUnlocked(code, unlocked: false);
				},
				Activate = delegate
				{
					d.SetDealerUnlocked(code, !d.IsDealerUnlocked(code));
				}
			});
		}
		list.Add(Action_("Unlock all dealers", delegate
		{
			d.UnlockAllDealers();
			Toast("All dealers unlocked");
		}));
		return list;
	}

	private static string ShortRoadId(string id)
	{
		int num = id.LastIndexOf("_ROADS_", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			return id;
		}
		return id.Substring(num + 1);
	}

	private List<Item> SavegameMenu()
	{
		VehicleManager i = Mgr;
		List<Item> list = new List<Item> { Head("PROFILE") };
		SaveData save = TDU2Session.Save;
		list.Add(Info("Nick", () => TDU2Session.Nick ?? "-"));
		list.Add(Info("Logged in", () => (!TDU2Session.LoggedIn) ? "NO" : "YES"));
		if (save == null)
		{
			list.Add(Info("no savegame loaded"));
			return list;
		}
		list.Add(Info("Island / position", () => (string.IsNullOrEmpty(save.island) ? "-" : save.island) + (save.hasPosition ? ("" + Num(save.px) + ", " + Num(save.py) + ", " + Num(save.pz) + "") : "")));
		list.Add(Info("Interior", () => (!string.IsNullOrEmpty(save.interior)) ? save.interior : "outside"));
		list.Add(Info("Saved at", () => (!string.IsNullOrEmpty(save.savedAt)) ? save.savedAt : "-"));
		list.Add(Head("MONEY"));
		list.Add(new Item
		{
			Label = "Money",
			Value = () => "$ " + save.money.ToString("N0", Inv),
			Change = delegate(int d)
			{
				save.money = Math.Max(0L, save.money + (long)d * 10000L);
			},
			Reset = delegate
			{
				save.money = 0L;
			}
		});
		list.Add(Action_("+ $ 1 000 000", delegate
		{
			save.money += 1000000L;
			Toast("Money: $ " + save.money.ToString("N0", Inv));
		}));
		list.Add(Head("CARS"));
		list.Add(Info("Own cars", () => save.ownedCars.Count + "" + (string.IsNullOrEmpty(save.currentCar) ? "-" : save.currentCar) + ""));
		list.Add(Bool("Starter car chosen", () => save.starterChosen, delegate(bool v)
		{
			save.starterChosen = v;
		}, def: true));
		list.Add(Head("STORY"));
		list.Add(new Item
		{
			Label = "Story step",
			Value = delegate
			{
				int num2 = StoryProgress.Get(TDU2Session.Nick);
				return num2.ToString(Inv) + "" + ((num2 >= 0) ? StoryProgress.StepName(num2) : "never started");
			},
			Change = delegate(int d)
			{
				int num = Math.Clamp(StoryProgress.Get(TDU2Session.Nick) + Math.Sign(d), -1, 11);
				StoryProgress.Set(TDU2Session.Nick, num);
				save.story = num;
			},
			Reset = delegate
			{
				StoryProgress.Set(TDU2Session.Nick, -1);
				save.story = -1;
			}
		});
		list.Add(Action_("Start the selected story step now", delegate
		{
			int step = StoryProgress.Get(TDU2Session.Nick);
			if (Story.Active)
			{
				Story.Go(step);
				Toast("Story: step " + step);
			}
			else
			{
				Toast("Story not running – the step starts with the next login");
			}
		}));
		list.Add(Bool("Story gave the California", () => StoryProgress.GaveStoryCar(TDU2Session.Nick), delegate(bool v)
		{
			StoryProgress.SetGaveStoryCar(TDU2Session.Nick, v);
		}, def: false));
		list.Add(Head("OTHER"));
		list.Add(Info("Houses with layout / furniture / materials", () => save.houses.Count + " / " + save.ownedFurniture.Count + " / " + save.ownedMaterials.Count));
		list.Add(Info("Clothes / sticker boards / liveries", () => save.ownedClothes.Count + " / " + save.stickerBoards.Count + " / " + save.liveries.Count));
		list.Add(Info("Bookmarks / favourite spots", () => save.bookmarks.Count + " / " + save.favoriteSpots.Count));
		list.Add(Action_("Clear last interior", delegate
		{
			save.interior = "";
			Toast("Interior cleared");
		}));
		list.Add(Head("SAVE / LOAD"));
		list.Add(Action_("Save to server now", delegate
		{
			if (!TDU2Session.LoggedIn)
			{
				Toast("Not logged in");
			}
			else
			{
				TDU2Session.SaveAsync();
				Toast("Saving to server");
			}
		}));
		list.Add(Action_("Write local DATA now", delegate
		{
			if (i?.Database == null)
			{
				Toast("Car database not loaded");
			}
			else
			{
				TDU2SaveGame.WriteAsync(TDU2Session.Nick, save, i.Database, "dev menu");
				Toast("Writing " + TDU2SaveGame.PathFor(TDU2Session.Nick));
			}
		}));
		list.Add(Info("Local DATA", () => (!TDU2SaveGame.Exists(TDU2Session.Nick)) ? "none" : TDU2SaveGame.PathFor(TDU2Session.Nick)));
		list.Add(Action_("Read local DATA", delegate
		{
			SaveData s;
			string why;
			if (i?.Database == null)
			{
				Toast("Car database not loaded");
			}
			else if (TDU2SaveGame.TryRead(TDU2Session.Nick, i.Database, out s, out why))
			{
				string s2 = "DATA: money " + s.money + ", cars " + s.ownedCars.Count + ", island " + s.island + ", story " + s.story + ", interior '" + s.interior + "'";
				Toast(s2);
			}
			else
			{
				Toast("DATA not readable: " + why);
			}
		}));
		return list;
	}

	private List<Item> OwnCarsMenu()
	{
		VehicleManager mgr = Mgr;
		SaveData save = TDU2Session.Save;
		List<Item> list = new List<Item> { Head("OWN CARS") };
		if (save == null)
		{
			list.Add(Info("no savegame loaded"));
			return list;
		}
		foreach (string item in new List<string>(save.ownedCars))
		{
			string f2 = item;
			CarPhysics car = mgr?.FindCar(f2);
			list.Add(new Item
			{
				Label = ((car != null) ? (car.Brand + "" + car.ModelName).Trim() : f2),
				Value = () => f2 + (string.Equals(save.currentCar, f2, StringComparison.OrdinalIgnoreCase) ? "" : ""),
				Activate = delegate
				{
					if (car != null)
					{
						DriveCar(car);
					}
				},
				Reset = delegate
				{
					save.ownedCars.Remove(f2);
					Toast(f2 + " removed from profile");
					Rebuild();
				}
			});
		}
		if (list.Count == 1)
		{
			list.Add(Info("no own cars"));
		}
		return list;
	}

	private List<Item> ControlsMenu()
	{
		List<Item> list = new List<Item> { Head("DEVICE") };
		list.Add(Info("Main device", () => (!TDU2InputDevices.HasDevice) ? "none" : (TDU2InputDevices.DeviceName + "" + TDU2InputDevices.DeviceJoy + "")));
		list.Add(Info("Detected as", delegate
		{
			if (!TDU2InputDevices.HasDevice)
			{
				return "-";
			}
			return (!TDU2InputDevices.IsWheel) ? "Gamepad" : "Wheel";
		}));
		list.Add(Info("Profile", () => (!string.IsNullOrEmpty(TDU2InputDevices.ProfileName)) ? TDU2InputDevices.ProfileName : "-"));
		list.Add(Info("Option 'Device'", () => TDU2InputDevices.Mode.ToString()));
		list.Add(Info("Separate shifter", () => (TDU2InputDevices.ShifterJoy <= 0) ? "none" : (TDU2InputDevices.ShifterName + "" + TDU2InputDevices.ShifterJoy + "")));
		list.Add(Head("ALL DEVICES (Unity joystick number)"));
		string[] names = Joysticks.Names;
		for (int j = 1; j <= Math.Min(TDU2InputDevices.JoyCount, names.Length); j++)
		{
			int joy = j;
			string name = string.IsNullOrEmpty(names[j - 1]) ? "-" : names[j - 1];
			list.Add(Info("J" + joy, () => name + " / " + Safe(() => TDU2InputDevices.RawAxis(joy, 1).ToString("+0.0;-0.0", Inv))));
		}
		list.Add(Head("RAW AXES"));
		for (int i = 1; i <= TDU2InputDevices.AxisCount; i++)
		{
			int ax = i;
			list.Add(Info("Axis " + ax + (TDU2InputDevices.IsHatAxis(ax) ? "" : ""), () => TDU2InputDevices.RawAxis(ax).ToString("+0.000;-0.000", Inv)));
		}
		list.Add(Head("RESULT"));
		list.Add(Info("Steer", () => TDU2InputDevices.Steer.ToString("+0.00;-0.00", Inv) + (TDU2InputDevices.IsWheel ? (" gain " + TDU2InputDevices.WheelSteerGain.ToString("0.00", Inv)) : "")));
		list.Add(Info("Throttle / Brake / Clutch", () => TDU2InputDevices.Throttle.ToString("0.00", Inv) + " / " + TDU2InputDevices.Brake.ToString("0.00", Inv) + " / " + TDU2InputDevices.Clutch.ToString("0.00", Inv)));
		list.Add(Info("H-shifter gear", delegate
		{
			int hGear = TDU2InputDevices.HGear;
			return (hGear >= 0) ? ((hGear != 0) ? hGear.ToString(Inv) : "N / none") : "R";
		}));
		list.Add(Info("Look / Move", () => TDU2InputDevices.Look.X.ToString("+0.00;-0.00", Inv) + "" + TDU2InputDevices.Look.Y.ToString("+0.00;-0.00", Inv) + " / " + TDU2InputDevices.Move.X.ToString("+0.00;-0.00", Inv) + "" + TDU2InputDevices.Move.Y.ToString("+0.00;-0.00", Inv)));
		list.Add(Head("BINDINGS"));
		string[] array = new string[5] { "accel", "brake", "left", "right", "clutch" };
		foreach (string text in array)
		{
			string a = text;
			list.Add(Info(a, () => TDU2InputDevices.BindingText(a)));
		}
		list.Add(Head("SETTINGS"));
		list.Add(Info("Steering sensitivity / dead zone", () => TDU2InputDevices.SteerLinearity.ToString("0.00", Inv) + " / " + TDU2InputDevices.DeadZone.ToString("0.00", Inv)));
		list.Add(Info("Throttle / brake / clutch linearity", () => TDU2InputDevices.ThrottleLinearity.ToString("0.00", Inv) + " / " + TDU2InputDevices.BrakeLinearity.ToString("0.00", Inv) + " / " + TDU2InputDevices.ClutchLinearity.ToString("0.00", Inv)));
		list.Add(Info("Mouse sensitivity / invert", () => TDU2InputDevices.MouseSensitivity.ToString("0.00", Inv) + " / " + (TDU2InputDevices.MouseInvert ? "yes" : "no")));
		list.Add(Head("DEBUG VIEW"));
		list.Add(Bool("Input overlay (axes, pedals, buttons)", () => InputOverlayVisible, delegate(bool v)
		{
			InputOverlayVisible = v;
		}, def: false));
		return list;
	}

	private List<Item> DebugViewMenu()
	{
		return new List<Item>
		{
			Head("CHOOSE A VIEW"),
			Menu("Race Debug View", RaceDebugViewMenu),
			Menu("Traffic Debug View", TrafficDebugViewMenu),
			Menu("Car Physics Debug View", CarDebugViewMenu),
			Menu("Collision Debug View", CollisionViewMenu),
			Menu("Camera / Free-fly", CameraDebugViewMenu),
			Head("QUICK MASTERS"),
			Bool("Traffic debug", () => TrafficConfig.ShowDebug, delegate(bool v)
			{
				TrafficConfig.ShowDebug = v;
			}, def: false),
			Bool("AI path system", () => _aiPaths, delegate(bool v)
			{
				_aiPaths = v;
			}, def: false),
			Bool("All car debug lines", () => AnyCarDebug, SetAllCarDebug, def: false),
			Bool("Show collision boxes", () => _collisionBoxes, delegate(bool v)
			{
				_collisionBoxes = v;
			}, def: false),
			Head(""),
			Info("More debug pages: Debug > Collision view / AI path / Savegame debug / Network traffic / Flash"),
			Info("Debug overlay: F12 · object under the crosshair: F10 · Toggle Control: " + ToggleKeyName()),
			Info("Lines are drawn without depth test")
		};
	}

	private List<Item> RaceDebugViewMenu()
	{
		List<Item> list = new List<Item> { Head("RACE DEBUG VIEW") };
		AddRaceDebugToggles(list);
		list.Add(Head(""));
		list.Add(Info("Race catalog / start: Tools > Race file"));
		return list;
	}

	private List<Item> TrafficDebugViewMenu()
	{
		Traffic tr = Traffic;
		List<Item> list = new List<Item> { Head("TRAFFIC DEBUG VIEW") };
		AddTrafficDebugToggles(list);
		list.Add(Head("AI PATH SYSTEM"));
		AddAiPathToggles(list);
		list.Add(Head(""));
		list.Add(Info("AI traffic cars", () => (tr == null) ? "-" : tr.CarCount.ToString(Inv)));
		list.Add(Info("Settings: Vehicle > Traffic · colors: Debug > AI path"));
		return list;
	}

	private List<Item> CarDebugViewMenu()
	{
		return new List<Item>
		{
			Head("CAR PHYSICS DEBUG VIEW"),
			Bool("All car debug lines", () => AnyCarDebug, SetAllCarDebug, def: false),
			Bool(" Suspension rays", () => _dbgRays, delegate(bool v)
			{
				_dbgRays = v;
			}, def: false),
			Bool(" Tire forces", () => _dbgForces, delegate(bool v)
			{
				_dbgForces = v;
			}, def: false),
			Bool(" Velocity / forward arrows", () => _dbgVelocity, delegate(bool v)
			{
				_dbgVelocity = v;
			}, def: false),
			Bool(" Wheel labels", () => _dbgWheelLabels, delegate(bool v)
			{
				_dbgWheelLabels = v;
			}, def: false),
			Head(""),
			Info("Settings: Vehicle > Physics settings · values: Vehicle > Physics debug")
		};
	}

	private List<Item> CameraDebugViewMenu()
	{
		List<Item> list = new List<Item> { Head("CAMERA / FREE-FLY") };
		list.Add(Bool("Free-fly camera", () => _terrain.FreeFly, delegate(bool v)
		{
			SetFreeFly(v);
		}, def: false));
		FlyCamera cam = _terrain.Camera;
		if (cam != null)
		{
			list.Add(Float("Fly speed", () => cam.Speed, delegate(float v)
			{
				cam.Speed = v;
			}, 10f, 10f, 2000f, 140f, "0", " m/s"));
			list.Add(Float("Fast multiplier", () => cam.FastMultiplier, delegate(float v)
			{
				cam.FastMultiplier = v;
			}, 0.5f, 1f, 20f, 6f, "0.0", " x"));
			list.Add(Info("Camera", delegate
			{
				MapStreamer streamer2 = Streamer;
				return (streamer2 == null) ? "-" : (Num(streamer2.ViewerGame.X) + " / " + Num(streamer2.ViewerGame.Y) + " / " + Num(streamer2.ViewerGame.Z));
			}));
		}
		list.Add(Action_("Put car at the camera", delegate
		{
			VehicleManager mgr = Mgr;
			MapStreamer streamer = Streamer;
			FlyCamera camera = _terrain.Camera;
			if (mgr?.Current == null || streamer == null || camera == null)
			{
				Toast("No car");
			}
			else
			{
				Vector3 forward = camera.Forward;
				forward.Y = 0f;
				mgr.TeleportSafe(streamer.ViewerGame, (forward.LengthSquared() > 0.0001f) ? Vector3.Normalize(forward) : Vector3.UnitZ);
				SetFreeFly(on: false);
				Toast("Car moved to the camera");
			}
		}));
		list.Add(Head(""));
		list.Add(Info("Free camera: RMB look · WASD move · Space/Ctrl up/down · Shift fast"));
		return list;
	}

	private void SetFreeFly(bool on)
	{
		_terrain.FreeFly = on;
		Toast(on ? "Free-fly camera on" : "Free-fly camera off");
	}

	private List<Item> FlashMenu()
	{
		_flashViewers.RemoveAll((VmfPlayer v) => !FlashHost.Players.Contains(v));
		List<Item> list = new List<Item> { Head("PLAYING FLASH") };
		int num = 0;
		foreach (VmfPlayer player in FlashHost.Players)
		{
			if (player?.Movie != null)
			{
				VmfPlayer pp = player;
				num++;
				string fileName = Path.GetFileName(player.BankPath ?? "");
				list.Add(Menu(fileName + (_flashViewers.Contains(player) ? "" : "") + (player.Visible ? "" : " [hidden]"), () => FlashPlayerMenu(pp)));
			}
		}
		if (num == 0)
		{
			list.Add(Info("no Flash movie active"));
		}
		list.Add(Head("LOAD MOVIE"));
		list.Add(Menu("Open frontend bank …", FlashLoadMenu));
		if (_flashViewers.Count > 0)
		{
			list.Add(Action_("Close loaded viewers " + _flashViewers.Count + "", delegate
			{
				foreach (VmfPlayer flashViewer in _flashViewers)
				{
					FlashHost.Remove(flashViewer);
				}
				_flashViewers.Clear();
				Toast("Viewers closed");
				Rebuild();
			}));
		}
		list.Add(Head("PROFILER"));
		list.Add(Info("Flash Profiler / Graph / Log", () => "Debug > Flash profiler"));
		return list;
	}

	private List<Item> FlashProfilerMenu()
	{
		List<Item> list = new List<Item> { Head("FLASH PROFILER") };
		list.Add(Info("Frame ms", () => VmfPlayer.LastFrameTimeMs.ToString("0.000", Inv)));
		list.Add(Info("Update/Frame parts", () => "Tick " + VmfPlayer.LastTickTimeMs.ToString("0.000", Inv) + " / Mouse " + VmfPlayer.LastMouseTimeMs.ToString("0.000", Inv) + " / Apply " + VmfPlayer.LastApplyTimeMs.ToString("0.000", Inv)));
		list.Add(Info("Draw ms", () => VmfPlayer.LastDrawTimeMs.ToString("0.000", Inv)));
		list.Add(Info("Raster integrate ms", () => VmfPlayer.LastRasterIntegrateTimeMs.ToString("0.000", Inv)));
		list.Add(Info("Draws / Shapes / Texts", () => VmfPlayer.LastDrawCalls + " / " + VmfPlayer.LastShapeDraws + " / " + VmfPlayer.LastTextDraws));
		list.Add(Info("Quad batches", () => VmfPlayer.LastQuadBatches.ToString(Inv)));
		list.Add(Info("Raster pending bytes", () => ((float)VmfPlayer.LastRasterPendingBytes / 1024f).ToString("0", Inv) + " KB"));
		list.Add(Info("Raster queue / done", () => VmfPlayer.LastRasterQueueCount + " / " + VmfPlayer.LastRasterDoneCount));
		list.Add(Bool("Auto-log to CSV", () => _flashLogAuto, delegate(bool v)
		{
			_flashLogAuto = v;
			_flashLogNext = _time;
		}, def: false));
		list.Add(Action_("Log now to CSV", delegate
		{
			VmfPlayer.AppendFlashLog();
			Toast("Logged to " + Path.GetFileName(VmfPlayer.GetFlashLogPath()));
		}));
		list.Add(Action_("Open log path", delegate
		{
			try
			{
				Process.Start("explorer.exe", "/select,\"" + VmfPlayer.GetFlashLogPath() + "\"");
			}
			catch (Exception)
			{
			}
		}));
		list.Add(Action_("Clear graph history", delegate
		{
			VmfPlayer.ClearFlashMetrics();
			Toast("Cleared");
		}));
		list.Add(Head("GRAPH"));
		float[] hist = VmfPlayer.GetFrameHistory();
		if (hist.Length == 0)
		{
			list.Add(Info("no samples yet"));
		}
		else
		{
			float max = 0f;
			float num = 0f;
			float[] array = hist;
			foreach (float num2 in array)
			{
				if (num2 > max)
				{
					max = num2;
				}
				num += num2;
			}
			if (max < 0.01f)
			{
				max = 0.01f;
			}
			float avg = num / (float)hist.Length;
			list.Add(Info("Samples", () => hist.Length.ToString(Inv)));
			list.Add(Info("Max / Avg", () => max.ToString("0.000", Inv) + " / " + avg.ToString("0.000", Inv) + " ms"));
			list.Add(Info("Graph", () => GraphStr(hist, 40, max)));
		}
		if (_flashLogAuto && _time >= _flashLogNext)
		{
			_flashLogNext = _time + 0.5;
			VmfPlayer.AppendFlashLog();
		}
		return list;
	}

	private static string GraphStr(float[] arr, int width, float max)
	{
		if (arr.Length == 0)
		{
			return "-";
		}
		StringBuilder stringBuilder = new StringBuilder(width + 2);
		stringBuilder.Append('[');
		for (int i = 0; i < width; i++)
		{
			int num = arr.Length - width + i;
			if (num < 0)
			{
				stringBuilder.Append(' ');
				continue;
			}
			float num2 = arr[num] / max;
			stringBuilder.Append((num2 >= 0.9f) ? '#' : ((num2 >= 0.7f) ? 'X' : ((num2 >= 0.5f) ? '=' : ((num2 >= 0.3f) ? '-' : ((num2 >= 0.1f) ? '.' : ' ')))));
		}
		stringBuilder.Append(']');
		return stringBuilder.ToString();
	}

	private List<Item> FlashLoadMenu()
	{
		List<Item> list = new List<Item>();
		string bnkDir = FlashHost.BnkDir;
		if (string.IsNullOrEmpty(bnkDir))
		{
			list.Add(Info("Bnk folder unknown"));
			return list;
		}
		string[] array = new string[3] { "hires", "medres", "lowres" };
		foreach (string text in array)
		{
			string text2 = Path.Combine(bnkDir, "frontend", text);
			if (!GameFiles.DirectoryExists(text2))
			{
				continue;
			}
			list.Add(Head(text.ToUpperInvariant()));
			string[] files = GameFiles.GetFiles(text2, "*.bnk");
			Array.Sort(files, (IComparer<string>?)StringComparer.OrdinalIgnoreCase);
			string[] array2 = files;
			foreach (string text3 in array2)
			{
				string path = text3;
				list.Add(Action_(Path.GetFileNameWithoutExtension(text3), delegate
				{
					LoadFlashViewer(path);
				}));
			}
			break;
		}
		if (list.Count == 0)
		{
			list.Add(Info("no frontend folder"));
		}
		return list;
	}

	private void LoadFlashViewer(string path)
	{
		VmfPlayer vmfPlayer = FlashHost.Load(path, -20);
		if (vmfPlayer == null)
		{
			Toast("Error: " + Path.GetFileName(path) + " not loadable ");
			return;
		}
		vmfPlayer.MouseEnabled = true;
		_flashViewers.Add(vmfPlayer);
		Toast("Loaded: " + Path.GetFileName(path) + " – labels under Flash Debug > movie > root");
	}

	private List<Item> FlashPlayerMenu(VmfPlayer p)
	{
		VmfPlayer p2 = p;
		List<Item> list = new List<Item>();
		if (p2 == null || p2.Root == null || !FlashHost.Players.Contains(p2))
		{
			list.Add(Info("Movie no longer exists"));
			return list;
		}
		list.Add(Head("CLIP " + Path.GetFileName(p2.BankPath ?? "")));
		list.Add(Bool("Visible", () => p2.Visible, delegate(bool v)
		{
			p2.Visible = v;
		}, def: true));
		list.Add(Bool("Mouse", () => p2.MouseEnabled, delegate(bool v)
		{
			p2.MouseEnabled = v;
		}, def: false));
		list.Add(Int("GUI Depth", () => p2.GuiDepth, delegate(int v)
		{
			p2.GuiDepth = v;
		}, -100, 100, 0));
		list.Add(Info("Focus", () => p2.GetVariable("/:Clip") ?? "-"));
		list.Add(Action_("Reset Movie", delegate
		{
			ResetFlash(p2);
		}));
		list.Add(Info("Note: labels/images from the root folder alter the menus/HUD – reset them here"));
		list.Add(Head("INPUT"));
		(string, string, string)[] array = new(string, string, string)[8]
		{
			("UP", "Move", "up"),
			("DOWN", "Move", "down"),
			("LEFT", "Move", "left"),
			("RIGHT", "Move", "right"),
			("A", "A", null),
			("B", "B", null),
			("X", "X", null),
			("Y", "Y", null)
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, string, string) tuple = array[i];
			string item = tuple.Item1;
			string item2 = tuple.Item2;
			string item3 = tuple.Item3;
			string f2 = item2;
			string a2 = item3;
			list.Add(Action_(item, delegate
			{
				bool flag = ((a2 != null) ? p2.Input(f2, a2) : p2.Input(f2));
				Toast(f2 + ((a2 != null) ? ("" + a2 + "") : "") + (flag ? " processed" : ": no function"));
			}));
		}
		list.AddRange(FlashClipItems(p2, "/", p2.Root));
		return list;
	}

	private void ResetFlash(VmfPlayer p)
	{
		if (p == null || string.IsNullOrEmpty(p.BankPath))
		{
			return;
		}
		if (Mgr?.Minimap?.Hud == p)
		{
			Toast("HUD is set up by the car – respawn the car to reload it");
			return;
		}
		try
		{
			bool visible = p.Visible;
			p.Load(p.BankPath);
			p.Visible = visible;
			if (!string.IsNullOrEmpty(FlashHost.BnkDir))
			{
				FlashLocalization.Apply(p, FlashLocalization.Load(FlashHost.BnkDir, FlashHost.Language));
			}
			Toast("Reloaded: " + Path.GetFileName(p.BankPath) + "");
		}
		catch (Exception ex)
		{
			Toast("Error: " + ex.Message);
		}
		Rebuild();
	}

	private static string? FlashLabel(VmfPlayer.Instance inst)
	{
		string result = null;
		int num = -1;
		foreach (KeyValuePair<string, int> label in inst.Timeline.Labels)
		{
			if (label.Value <= inst.Frame && label.Value > num)
			{
				num = label.Value;
				result = label.Key;
			}
		}
		return result;
	}

	private List<Item> FlashClipItems(VmfPlayer p, string path, VmfPlayer.Instance? inst)
	{
		VmfPlayer.Instance inst2 = inst;
		VmfPlayer p2 = p;
		string path2 = path;
		List<Item> list = new List<Item>();
		if (inst2 == null)
		{
			list.Add(Info("Clip no longer exists"));
			return list;
		}
		list.Add(Head("CLIP " + path2));
		list.Add(Info("Frame / label", () => inst2.Frame + 1 + " / " + inst2.Timeline.FrameCount + " [" + (FlashLabel(inst2) ?? "-") + "]" + (inst2.Playing ? " playing" : " stopped")));
		list.Add(Bool("Play", () => inst2.Playing, delegate(bool v)
		{
			inst2.Playing = v;
		}, def: true));
		list.Add(Int("Frame", () => inst2.Frame + 1, delegate(int v)
		{
			p2.GotoFrame(inst2, Math.Clamp(v - 1, 0, Math.Max(0, inst2.Timeline.FrameCount - 1)), runActions: true);
		}, 1, Math.Max(1, inst2.Timeline.FrameCount), 1));
		VmfPlayer.DisplayObject owner = inst2.Owner;
		if (owner != null)
		{
			list.Add(Bool("Visible", () => owner.Visible, delegate(bool v)
			{
				owner.Visible = v;
			}, def: true));
		}
		if (inst2.Timeline.Labels.Count > 0)
		{
			list.Add(Head("LABELS"));
			List<KeyValuePair<string, int>> list2 = new List<KeyValuePair<string, int>>(inst2.Timeline.Labels);
			list2.Sort((KeyValuePair<string, int> a, KeyValuePair<string, int> b) => a.Value.CompareTo(b.Value));
			foreach (KeyValuePair<string, int> item in list2)
			{
				string lab = item.Key;
				list.Add(new Item
				{
					Label = lab + "" + (item.Value + 1) + "",
					Value = () => (!(FlashLabel(inst2) == lab)) ? "" : "<- active",
					Activate = delegate
					{
						p2.GotoLabel(path2, lab, play: true);
						Toast(path2 + " -> " + lab + "");
					},
					Change = delegate
					{
						p2.GotoLabel(path2, lab);
						Toast(path2 + " -> " + lab + "");
					}
				});
			}
		}
		List<Item> list3 = new List<Item>();
		foreach (VmfPlayer.DisplayObject value in inst2.List.Values)
		{
			if (value.Sprite != null)
			{
				string text = value.Name ?? ("#" + value.Depth);
				string cp = ((path2 == "/") ? "/" : (path2 + "/")) + text;
				VmfPlayer.Instance sub = value.Sprite;
				bool flag = value.Visible && (!(value.Cx.Mul.w <= 0.003f) || !(value.Cx.Add.w <= 0.003f));
				list3.Add(Menu(text + (flag ? "" : " [hidden]") + "" + sub.Timeline.FrameCount + " fr., " + sub.Timeline.Labels.Count + " labels", () => FlashClipItems(p2, cp, sub)));
			}
		}
		if (list3.Count > 0)
		{
			list.Add(Head("SUB-CLIPS"));
			list.AddRange(list3);
		}
		return list;
	}

	private List<Item> AudioMenu()
	{
		VehicleManager i = Mgr;
		List<Item> list = new List<Item> { Head("NOW PLAYING") };
		list.Add(Info("Audio output", () => (AudioEngine.Paused ? "paused" : "running") + " · " + AudioEngine.VoiceCount + " voices · " + 44100 + " Hz"));
		list.Add(Info("Menu music", () => FrontendMusic.Current ?? "-"));
		list.Add(Info("Spot music", () => SpotMusic.Current ?? "-"));
		list.Add(Info("Radio", delegate
		{
			Radio radio2 = GAME_MODE_FREERIDE.Radio;
			if (radio2 == null)
			{
				return "-";
			}
			string text = ((radio2.CurrentArtist.Length > 0) ? (radio2.CurrentArtist + " - " + radio2.CurrentTitle) : radio2.CurrentTitle);
			return radio2.CurrentStation.ToString() + " vol " + radio2.CurrentVolume + (radio2.Muted ? "" : "") + ((text.Length > 0) ? (" · " + text) : "");
		}));
		list.Add(Info("Ambience", delegate
		{
			Ambience ambience4 = GAME_MODE_FREERIDE.Ambience;
			return (ambience4 != null) ? (ambience4.Island + " · zone type " + ((ambience4.CurrentType >= 0) ? ambience4.CurrentType.ToString(Inv) : "-") + " · vol " + Num(ambience4.Volume, "0.00")) : "-";
		}));
		list.Add(Info("Car engine", delegate
		{
			VehicleAudio vehicleAudio = i?.Audio;
			return (vehicleAudio == null) ? "-" : ((vehicleAudio.EngineOn ? "on" : "off") + (vehicleAudio.InteriorView ? " · interior view" : ""));
		}));
		list.Add(Head("LISTENER VOLUMES"));
		list.Add(Float("Master", () => AudioEngine.MasterVolume, delegate(float v)
		{
			AudioEngine.MasterVolume = v;
		}, 0.05f, 0f, 1f, 0.85f, "0.00"));
		list.Add(Float("Menu music", () => FrontendMusic.Volume, delegate(float v)
		{
			FrontendMusic.Volume = v;
		}, 0.05f, 0f, 1f, 0.35f, "0.00"));
		list.Add(Float("Spot music", () => SpotMusic.Volume, delegate(float v)
		{
			SpotMusic.Volume = v;
		}, 0.05f, 0f, 1f, 0.6f, "0.00"));
		list.Add(Float("Interface sounds", () => FrontendSounds.Volume, delegate(float v)
		{
			FrontendSounds.Volume = v;
		}, 0.05f, 0f, 1f, 0.4f, "0.00"));
		list.Add(Bool("Interface sounds on", () => FrontendSounds.Enabled, delegate(bool v)
		{
			FrontendSounds.Enabled = v;
		}, def: true));
		if (i != null)
		{
			list.Add(Float("Car", () => i.Volume, delegate(float v)
			{
				i.Volume = v;
				if (i.Audio != null)
				{
					i.Audio.MasterVolume = v;
				}
			}, 0.02f, 0f, 1f, 0.34f, "0.00"));
			list.Add(Float("Tyre squeal", () => i.SquealVolume, delegate(float v)
			{
				i.SquealVolume = v;
				if (i.Audio != null)
				{
					i.Audio.SquealFactor = v;
				}
			}, 0.05f, 0f, 2f, 0.3f, "0.00"));
			list.Add(Float("Gear shift", () => i.GearShiftVolume, delegate(float v)
			{
				i.GearShiftVolume = v;
				if (i.Audio != null)
				{
					i.Audio.GearShiftFactor = v;
				}
			}, 0.05f, 0f, 2f, 0.3f, "0.00"));
		}
		list.Add(new Item
		{
			Label = "Ambience",
			Value = delegate
			{
				Ambience ambience3 = GAME_MODE_FREERIDE.Ambience;
				return (ambience3 == null) ? "-" : Num(ambience3.Volume, "0.00");
			},
			Change = delegate(int d)
			{
				Ambience ambience2 = GAME_MODE_FREERIDE.Ambience;
				if (ambience2 != null)
				{
					ambience2.Volume = Math.Clamp(ambience2.Volume + 0.05f * (float)d, 0f, 1f);
				}
			},
			Reset = delegate
			{
				Ambience ambience = GAME_MODE_FREERIDE.Ambience;
				if (ambience != null)
				{
					ambience.Volume = 0.1f;
				}
			}
		});
		list.Add(new Item
		{
			Label = "Radio volume",
			Value = delegate
			{
				Radio radio = GAME_MODE_FREERIDE.Radio;
				return (radio == null) ? "-" : radio.CurrentVolume.ToString(Inv);
			},
			Change = delegate(int d)
			{
				GAME_MODE_FREERIDE.Radio?.ChangeVolume(Math.Sign(d));
			}
		});
		list.Add(Head("RADIO"));
		list.Add(Action_("Radio on/off", delegate
		{
			GAME_MODE_FREERIDE.Radio?.Toggle();
		}));
		list.Add(Action_("Next station", delegate
		{
			GAME_MODE_FREERIDE.Radio?.ChangeStation(1);
		}));
		list.Add(Action_("Previous station", delegate
		{
			GAME_MODE_FREERIDE.Radio?.ChangeStation(-1);
		}));
		return list;
	}

	private void OpenEditor()
	{
		if (!_editorRoots)
		{
			_editorRoots = true;
			SceneEditor.RenderToGame = (Vector3 r) => Streamer?.RenderToGame(r) ?? r;
			SceneEditor.CameraGame = () => Streamer?.ViewerGame ?? Vector3.Zero;
			SceneEditor.SetRoot("world", delegate
			{
				MapStreamer streamer = Streamer;
				return (streamer == null) ? null : SceneAdapters.World(streamer);
			});
			SceneEditor.SetRoot("player", EditorPlayerCar);
			SceneEditor.SetRoot("traffic", EditorTraffic);
			SceneEditor.SetRoot("remote", EditorRemoteCars);
			SceneEditor.SetRoot("avatars", () => SceneAdapters.Avatars(DescribeAvatar));
			SceneEditor.FocusCamera = EditorFocusCamera;
		}
		SceneEditor.Open();
		Toast("Editor open");
	}

	private SceneNode? EditorPlayerCar()
	{
		VehicleManager i = Mgr;
		CarModel model = i?.Model;
		if (i == null || model == null)
		{
			return null;
		}
		string car = i.CurrentCar?.ModelName ?? "?";
		return SceneAdapters.Car("g:player", "Player Car " + car + "", "Own Vehicle", model, () => Mgr?.Model == model, delegate(InspectorBuilder b)
		{
			b.Section("Personal Vehicle");
			b.Info("Car", car);
			b.Info("Position", i.PositionGame);
			b.Info("Direction of travel", i.ForwardGame);
			b.Info("Color / Wheels / Interior", i.ColorIndex + " / " + i.RimId + " / " + i.InteriorId);
			b.Info("Engine Running", i.EngineRunning ? "yes" : "no");
		});
	}

	private SceneNode? EditorTraffic()
	{
		Traffic t = _vehicles.Traffic;
		if (t == null)
		{
			return null;
		}
		return SceneAdapters.Toggle(SceneAdapters.Group("g:traffic", "Traffic", () => t.EditorCars().Count((Traffic.EditorCar c) => c.Model != null), () => (from c in t.EditorCars()
			where c.Model != null
			select c).Select(delegate(Traffic.EditorCar c)
		{
			CarModel model = c.Model;
			uint id = c.Id;
			return SceneAdapters.Car("g:traffic/" + id.ToString(CultureInfo.InvariantCulture), c.Type + " #" + id.ToString(CultureInfo.InvariantCulture), "Traffic car", model, () => _vehicles.Traffic?.EditorCars().Exists((Traffic.EditorCar x) => x.Id == id && x.Model == model) ?? false, delegate(InspectorBuilder b)
			{
				Traffic.EditorCar? editorCar = _vehicles.Traffic?.EditorCars().Find((Traffic.EditorCar x) => x.Id == id);
				b.Section("Traffic car");
				b.Info("Id", id.ToString(CultureInfo.InvariantCulture));
				b.Info("Type", c.Type);
				if (editorCar.HasValue)
				{
					Traffic.EditorCar valueOrDefault = editorCar.GetValueOrDefault();
					if (valueOrDefault.Id == id)
					{
						b.Info("Position", valueOrDefault.Position);
						b.Info("Speed", (valueOrDefault.Speed * 3.6f).ToString("0", CultureInfo.InvariantCulture) + " km/h");
						b.Info("Mode", valueOrDefault.Mode switch
						{
							0 => "Driving", 
							1 => "Crash", 
							2 => "Idle", 
							_ => valueOrDefault.Mode.ToString(CultureInfo.InvariantCulture), 
						});
						b.Info("Visible", valueOrDefault.Visible ? "yes" : "no");
						b.Info("Own", valueOrDefault.Mine ? "yes" : "no");
					}
				}
			});
		}), delegate(InspectorBuilder b)
		{
			List<Traffic.EditorCar> list = t.EditorCars();
			b.Section("Traffic");
			b.Info("Cars", list.Count.ToString(CultureInfo.InvariantCulture));
			b.Info("With model", list.Count((Traffic.EditorCar c) => c.Model != null).ToString(CultureInfo.InvariantCulture));
			b.Info("Visible", list.Count((Traffic.EditorCar c) => c.Visible).ToString(CultureInfo.InvariantCulture));
		}, null, "Traffic"), () => t.EditorCars().Exists((Traffic.EditorCar c) => c.Model != null && c.Model.Visual.Active), delegate(bool v)
		{
			foreach (Traffic.EditorCar item in t.EditorCars())
			{
				if (item.Model != null)
				{
					item.Model.Visual.Active = v;
				}
			}
		});
	}

	private SceneNode? EditorRemoteCars()
	{
		if (!RemoteCars.All.Any())
		{
			return null;
		}
		return SceneAdapters.Toggle(SceneAdapters.Group("g:remote", "Remote Players", () => RemoteCars.All.Count((RemoteCars.Remote r) => r.Model != null), () => RemoteCars.All.Where((RemoteCars.Remote r) => r.Model != null).ToList().Select(delegate(RemoteCars.Remote r)
		{
			CarModel model = r.Model;
			int id = r.Id;
			string car = r.Assets?.Car?.ModelName ?? "?";
			return SceneAdapters.Car("g:remote/" + id.ToString(CultureInfo.InvariantCulture), "Player " + id.ToString(CultureInfo.InvariantCulture) + "" + car + "", "Remote player car", model, () => RemoteCars.Get(id)?.Model == model, delegate(InspectorBuilder b)
			{
				b.Section("Remote player");
				b.Info("Id", id.ToString(CultureInfo.InvariantCulture));
				b.Info("Car", car);
				b.Info("Position", r.VisPos);
				b.Info("Visible", r.Visible ? "yes" : "no");
				b.Info("Camera distance", r.CamDistance.ToString("0.0", CultureInfo.InvariantCulture) + " m");
			});
		}), null, null, "Remote players"), () => RemoteCars.All.Any((RemoteCars.Remote r) => r.Model != null && r.Model.Visual.Active), delegate(bool v)
		{
			foreach (RemoteCars.Remote item in RemoteCars.All)
			{
				if (item.Model != null)
				{
					item.Model.Visual.Active = v;
				}
			}
		});
	}

	private static (string Name, string Type)? DescribeAvatar(AvatarModel a)
	{
		foreach (InteriorNpcs.Npc npc in InteriorNpcs.Npcs)
		{
			if (npc.Avatar == a)
			{
				return ((!string.IsNullOrEmpty(npc.Name)) ? npc.Name : (string.IsNullOrEmpty(npc.ConfigName) ? "Character" : npc.ConfigName), "Interior character");
			}
		}
		return null;
	}

	private bool EditorFocusCamera(Vector3 centerGame, float radius)
	{
		MapStreamer streamer = Streamer;
		FlyCamera camera = _terrain.Camera;
		if (streamer == null || camera == null)
		{
			return false;
		}
		if (!_terrain.FreeFly)
		{
			SetFreeFly(on: true);
		}
		Vector3 vector = streamer.GameToRender(centerGame);
		Vector3 value = vector - camera.Position;
		if (value.LengthSquared() < 0.0001f)
		{
			value = camera.Forward;
		}
		value = Vector3.Normalize(value);
		if (MathF.Abs(value.Y) > 0.87f)
		{
			value.Y = MathF.CopySign(0.87f, value.Y);
			value = Vector3.Normalize(value);
		}
		float num = MathF.Max(radius * 2.2f, 4f);
		camera.Position = vector - value * num;
		camera.Yaw = MathF.Atan2(value.X, value.Z);
		camera.Pitch = Math.Clamp(MathF.Asin(Math.Clamp(value.Y, -1f, 1f)), -1.5f, 1.5f);
		return true;
	}

	private void EditorUpdate()
	{
		if (!SceneEditor.ClickSelect)
		{
			return;
		}
		InputState input = _window.Input;
		if (!input.LeftMousePressed || !input.IsKeyDown(17) || _r.Width <= 0 || _r.Height <= 0)
		{
			return;
		}
		float px = (float)_r.Width * 0.5f;
		float py = (float)_r.Height * 0.5f;
		if (!input.CursorLocked)
		{
			Paradise.Core.Flash.Vector2 mousePosition = FlashInput.mousePosition;
			int width = Screen.width;
			int height = Screen.height;
			if (width > 0 && height > 0)
			{
				px = mousePosition.x * (float)_r.Width / (float)width;
				py = ((float)height - mousePosition.y) * (float)_r.Height / (float)height;
			}
		}
		if (_terrain.ScreenRay(px, py, out var origin, out var dir))
		{
			SceneEditor.PickRay(origin, dir);
		}
	}

	private static string SgFile(string ext, string suffix = "")
	{
		return Path.Combine(SgDebugDir, SafeFile(SgNick) + suffix + "." + ext);
	}

	private static string SafeFile(string name)
	{
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		foreach (char oldChar in invalidFileNameChars)
		{
			name = name.Replace(oldChar, '_');
		}
		return name;
	}

	private static string SgFileInfo(string path)
	{
		try
		{
			return File.Exists(path) ? (new FileInfo(path).Length.ToString("N0", Inv) + " bytes · " + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", Inv)) : "none";
		}
		catch (Exception ex)
		{
			return ex.Message;
		}
	}

	private List<Item> SavegameDebugMenu()
	{
		return new List<Item>
		{
			Head("PROFILE / FILES"),
			Info("Profile", () => TDU2Session.Nick ?? "-"),
			Info("Session", delegate
			{
				if (!TDU2Session.LoggedIn)
				{
					return "not logged in";
				}
				return (!TDU2Session.Offline) ? ("server · session " + ShortId(TDU2Session.SessionId)) : "offline profile";
			}),
			Info("Token", () => (!string.IsNullOrEmpty(TDU2Session.Token)) ? ("present " + TDU2Session.Token.Length + " chars") : "-"),
			Info("Local DATA", () => SgFileInfo(TDU2SaveGame.PathFor(SgNick))),
			Info("JSON export", () => SgFileInfo(SgJsonPath)),
			Info("XMBF export", () => SgFileInfo(SgXmbfPath)),
			Info("DATA backup", () => SgFileInfo(SgDataBackupPath)),
			Info("Debug folder", () => SgDebugDir),
			Head("SAVE / LOAD"),
			Action_("Save to server + local DATA", delegate
			{
				if (!TDU2Session.LoggedIn)
				{
					Toast("Not logged in");
				}
				else
				{
					TDU2Session.SaveAsync();
					Toast("Saving game");
				}
			}),
			Action_("Write local DATA now", delegate
			{
				if (TDU2Session.Save == null)
				{
					Toast("No savegame loaded");
				}
				else if (Mgr?.Database == null)
				{
					Toast("Car database not loaded");
				}
				else
				{
					TDU2SaveGame.WriteAsync(SgNick, TDU2Session.Save, Mgr.Database, "savegame debug");
					Toast("Writing " + TDU2SaveGame.PathFor(SgNick));
				}
			}),
			Action_("Import local DATA -> session", delegate
			{
				SaveData s3;
				string why3;
				if (Mgr?.Database == null)
				{
					Toast("Car database not loaded");
				}
				else if (!TDU2SaveGame.TryRead(SgNick, Mgr.Database, out s3, out why3))
				{
					Toast("DATA not readable: " + why3);
				}
				else
				{
					if (TDU2Session.Save != null)
					{
						TDU2SaveGame.KeepServerOnly(s3, TDU2Session.Save);
					}
					TDU2Session.ApplySave(s3);
					RefreshSgViews();
					Toast("DATA imported: $" + s3.money + ", " + s3.ownedCars.Count + " cars, island " + s3.island + "");
				}
			}),
			Action_("Read local DATA", delegate
			{
				SaveData s2;
				string why2;
				if (Mgr?.Database == null)
				{
					Toast("Car database not loaded");
				}
				else if (TDU2SaveGame.TryRead(SgNick, Mgr.Database, out s2, out why2))
				{
					string text = "DATA: $" + s2.money + ", " + s2.ownedCars.Count + " cars, " + s2.island + ", story " + s2.story + ", interior '" + s2.interior + "'";
					Log.Info(text);
					Toast(text);
				}
				else
				{
					Toast("DATA not readable: " + why2);
				}
			}),
			Head("EXPORT"),
			Action_("Export live SaveData -> JSON", delegate
			{
				if (TDU2Session.Save == null)
				{
					Toast("No savegame loaded");
					return;
				}
				try
				{
					Directory.CreateDirectory(SgDebugDir);
					File.WriteAllText(SgJsonPath, JsonSerializer.Serialize(TDU2Session.Save, SgPrettyJson));
					RefreshSgViews();
					Toast("JSON exported: " + SgJsonPath);
				}
				catch (Exception ex6)
				{
					Toast("JSON export failed: " + ex6.Message);
				}
			}),
			Action_("Export local DATA -> decrypted XMBF", delegate
			{
				if (!TDU2SaveGame.Exists(SgNick))
				{
					Toast("No local DATA");
					return;
				}
				try
				{
					Directory.CreateDirectory(SgDebugDir);
					DecryptedSave decryptedSave = SaveCrypto.Decrypt(File.ReadAllBytes(TDU2SaveGame.PathFor(SgNick)), SgNick, "DATA");
					File.WriteAllBytes(SgXmbfPath, decryptedSave.Xmbf);
					_sgXmbfRoot = Xmbf.Decode(decryptedSave.Xmbf).Root;
					Toast("XMBF exported: " + SgXmbfPath + "" + decryptedSave.Xmbf.Length + " bytes");
				}
				catch (Exception ex5)
				{
					Toast("XMBF export failed: " + ex5.Message);
				}
			}),
			Action_("Build XMBF from live SaveData -> file", delegate
			{
				if (TDU2Session.Save == null || Mgr?.Database == null)
				{
					Toast("Savegame or car database missing");
					return;
				}
				try
				{
					Directory.CreateDirectory(SgDebugDir);
					string info;
					byte[] array = TDU2SaveGame.Build(SgNick, TDU2Session.Save, Mgr.Database, out info);
					File.WriteAllBytes(SgBuiltXmbfPath, array);
					Toast("XMBF built " + array.Length + " bytes: " + info);
				}
				catch (Exception ex4)
				{
					Toast("XMBF build failed: " + ex4.Message);
				}
			}),
			Action_("Copy local DATA -> .DATA backup", delegate
			{
				if (!TDU2SaveGame.Exists(SgNick))
				{
					Toast("No local DATA");
					return;
				}
				try
				{
					Directory.CreateDirectory(SgDebugDir);
					File.Copy(TDU2SaveGame.PathFor(SgNick), SgDataBackupPath, overwrite: true);
					Toast("DATA backup: " + SgDataBackupPath);
				}
				catch (Exception ex3)
				{
					Toast("DATA backup failed: " + ex3.Message);
				}
			}),
			Head("IMPORT"),
			Action_("Import exported JSON -> session", delegate
			{
				if (!File.Exists(SgJsonPath))
				{
					Toast("No JSON export: " + SgJsonPath);
					return;
				}
				try
				{
					SaveData saveData = NetJson.FromJson<SaveData>(File.ReadAllText(SgJsonPath));
					if (saveData == null)
					{
						Toast("JSON is empty");
					}
					else
					{
						TDU2Session.ApplySave(saveData);
						RefreshSgViews();
						Toast("JSON imported: $" + saveData.money + ", " + saveData.ownedCars.Count + " cars ");
					}
				}
				catch (Exception ex2)
				{
					Toast("JSON import failed: " + ex2.Message);
				}
			}),
			Action_("Import exported XMBF -> DATA + session", delegate
			{
				if (!File.Exists(SgXmbfPath))
				{
					Toast("No XMBF export: " + SgXmbfPath);
					return;
				}
				try
				{
					byte[] xmbf = File.ReadAllBytes(SgXmbfPath);
					XmbfDocument xmbfDocument = Xmbf.Decode(xmbf);
					byte[] bytes = SaveCrypto.Encrypt(xmbf, SgNick, "DATA");
					Directory.CreateDirectory(Path.GetDirectoryName(TDU2SaveGame.PathFor(SgNick)));
					File.WriteAllBytes(TDU2SaveGame.PathFor(SgNick), bytes);
					_sgXmbfRoot = xmbfDocument.Root;
					string why = "car database not loaded";
					if (Mgr?.Database != null && TDU2SaveGame.TryRead(SgNick, Mgr.Database, out var s, out why))
					{
						if (TDU2Session.Save != null)
						{
							TDU2SaveGame.KeepServerOnly(s, TDU2Session.Save);
						}
						TDU2Session.ApplySave(s);
						Toast("XMBF imported: " + s.ownedCars.Count + " cars → DATA + session");
					}
					else
					{
						Toast("XMBF imported to DATA " + why + "");
					}
					RefreshSgViews();
				}
				catch (Exception ex)
				{
					Toast("XMBF import failed: " + ex.Message);
				}
			}),
			Head("VIEW DATA"),
			Menu("View live SaveData", ViewLiveSaveDataMenu),
			Menu("View JSON export", ViewJsonExportMenu),
			Menu("View local DATA", ViewXmbfMenu),
			Action_("Refresh data views", delegate
			{
				RefreshSgViews();
				Toast("Data views refreshed");
			}),
			Info("Last view status", () => _sgViewInfo),
			Head(""),
			Info("JSON = server/NetJson format · XMBF = original unencrypted PLAYERSAVE/DATA"),
			Info("Export/import only changes the live session and local files – use Save to server to persist.")
		};
	}

	private static string ShortId(string id)
	{
		if (!string.IsNullOrEmpty(id))
		{
			if (id.Length > 8)
			{
				return id.Substring(0, 8) + "…";
			}
			return id;
		}
		return "-";
	}

	private void RefreshSgViews()
	{
		_sgLiveDoc?.Dispose();
		_sgLiveDoc = null;
		_sgFileDoc?.Dispose();
		_sgFileDoc = null;
		_sgXmbfRoot = null;
		_sgViewInfo = "no data loaded";
		try
		{
			if (TDU2Session.Save != null)
			{
				_sgLiveDoc = JsonDocument.Parse(NetJson.ToJson(TDU2Session.Save));
				_sgViewInfo = "live SaveData loaded";
			}
		}
		catch (Exception ex)
		{
			_sgViewInfo = "live JSON: " + ex.Message;
		}
		try
		{
			if (File.Exists(SgJsonPath))
			{
				_sgFileDoc = JsonDocument.Parse(File.ReadAllText(SgJsonPath));
			}
		}
		catch (Exception ex2)
		{
			_sgViewInfo = "export JSON: " + ex2.Message;
		}
		try
		{
			if (TDU2SaveGame.Exists(SgNick) && Mgr?.Database != null)
			{
				byte[] file = File.ReadAllBytes(TDU2SaveGame.PathFor(SgNick));
				DecryptedSave decryptedSave = SaveCrypto.Decrypt(file, SgNick, "DATA");
				_sgXmbfRoot = Xmbf.Decode(decryptedSave.Xmbf).Root;
			}
		}
		catch (Exception ex3)
		{
			_sgViewInfo = "local DATA: " + ex3.Message;
		}
	}

	private List<Item> ViewLiveSaveDataMenu()
	{
		if (_sgLiveDoc == null)
		{
			RefreshSgViews();
		}
		List<Item> list = new List<Item> { Head("LIVE SAVEDATA · " + SgNick) };
		if (_sgLiveDoc == null)
		{
			list.Add(Info(_sgViewInfo));
			return list;
		}
		AddJsonItems(list, _sgLiveDoc.RootElement, 0);
		return list;
	}

	private List<Item> ViewJsonExportMenu()
	{
		if (_sgFileDoc == null)
		{
			RefreshSgViews();
		}
		List<Item> list = new List<Item> { Head("JSON EXPORT · " + SgJsonPath) };
		if (_sgFileDoc == null)
		{
			list.Add(Info(_sgViewInfo));
			return list;
		}
		AddJsonItems(list, _sgFileDoc.RootElement, 0);
		return list;
	}

	private List<Item> JsonNodeMenu(JsonElement e, string title, int depth)
	{
		List<Item> list = new List<Item> { Head(title.ToUpperInvariant()) };
		AddJsonItems(list, e, depth);
		return list;
	}

	private void AddJsonItems(List<Item> l, JsonElement e, int depth)
	{
		if (depth > 6)
		{
			l.Add(Info("[max depth reached]"));
		}
		else if (e.ValueKind == JsonValueKind.Object)
		{
			List<JsonProperty> props = new List<JsonProperty>();
			foreach (JsonProperty item in e.EnumerateObject())
			{
				props.Add(item);
			}
			if (props.Count <= 25)
			{
				foreach (JsonProperty item2 in props)
				{
					AddJsonEntry(l, item2.Name, item2.Value, depth);
				}
				return;
			}
			for (int i = 0; i < props.Count; i += 25)
			{
				int f2 = i;
				int t2 = Math.Min(i + 25, props.Count);
				l.Add(Menu("Fields " + (f2 + 1) + "–" + t2 + " / " + props.Count, () => JsonFieldsMenu(props, f2, t2, depth)));
			}
		}
		else if (e.ValueKind == JsonValueKind.Array)
		{
			List<JsonElement> items = new List<JsonElement>();
			foreach (JsonElement item3 in e.EnumerateArray())
			{
				items.Add(item3);
			}
			if (items.Count <= 25)
			{
				for (int j = 0; j < items.Count; j++)
				{
					AddJsonEntry(l, "[" + j + "]", items[j], depth);
				}
				return;
			}
			for (int k = 0; k < items.Count; k += 25)
			{
				int f = k;
				int t = Math.Min(k + 25, items.Count);
				l.Add(Menu("Items " + (f + 1) + "–" + t + " / " + items.Count, () => JsonItemsMenu(items, f, t, depth)));
			}
		}
		else
		{
			l.Add(Info("Value", () => JsonScalar(e)));
		}
	}

	private List<Item> JsonFieldsMenu(List<JsonProperty> props, int from, int to, int depth)
	{
		List<Item> list = new List<Item> { Head("FIELDS " + (from + 1) + "–" + to) };
		for (int i = from; i < to; i++)
		{
			AddJsonEntry(list, props[i].Name, props[i].Value, depth);
		}
		return list;
	}

	private List<Item> JsonItemsMenu(List<JsonElement> items, int from, int to, int depth)
	{
		List<Item> list = new List<Item> { Head("ITEMS " + (from + 1) + "–" + to) };
		for (int i = from; i < to; i++)
		{
			AddJsonEntry(list, "[" + i + "]", items[i], depth);
		}
		return list;
	}

	private void AddJsonEntry(List<Item> l, string name, JsonElement e, int depth)
	{
		string name2 = name;
		if (e.ValueKind == JsonValueKind.Object || e.ValueKind == JsonValueKind.Array)
		{
			int num = ((e.ValueKind == JsonValueKind.Array) ? e.GetArrayLength() : CountJsonFields(e));
			string text = ((e.ValueKind == JsonValueKind.Array) ? "array" : "object");
			l.Add(Menu(name2 + "" + text + ", " + num + "", () => JsonNodeMenu(e, name2, depth + 1)));
		}
		else
		{
			l.Add(Info(name2, () => JsonScalar(e)));
		}
	}

	private static int CountJsonFields(JsonElement e)
	{
		int num = 0;
		foreach (JsonProperty item in e.EnumerateObject())
		{
			num++;
		}
		return num;
	}

	private static string JsonScalar(JsonElement e)
	{
		string text = (e.ValueKind switch
		{
			JsonValueKind.String => e.GetString() ?? "", 
			JsonValueKind.True => "true", 
			JsonValueKind.False => "false", 
			JsonValueKind.Null => "null", 
			JsonValueKind.Undefined => "undefined", 
			_ => e.GetRawText(), 
		}).Replace("\r", "").Replace("\n", "");
		if (text.Length > 140)
		{
			return text.Substring(0, 140) + "…";
		}
		return text;
	}

	private List<Item> ViewXmbfMenu()
	{
		if (_sgXmbfRoot == null)
		{
			RefreshSgViews();
		}
		List<Item> list = new List<Item> { Head("LOCAL DATA · XMBF TREE") };
		if (_sgXmbfRoot == null)
		{
			list.Add(Info(_sgViewInfo));
			return list;
		}
		AddXItems(list, _sgXmbfRoot, 0);
		return list;
	}

	private List<Item> XNodeMenu(XValue v, string title, int depth)
	{
		List<Item> list = new List<Item> { Head(title.ToUpperInvariant()) };
		AddXItems(list, v, depth);
		return list;
	}

	private void AddXItems(List<Item> l, XValue v, int depth)
	{
		if (depth > 6)
		{
			l.Add(Info("[max depth reached]"));
			return;
		}
		XObject o = v as XObject;
		if (o != null)
		{
			List<string> keys = new List<string>(o.Keys);
			if (keys.Count <= 25)
			{
				foreach (string item in keys)
				{
					AddXEntry(l, item, o[item], depth);
				}
				return;
			}
			for (int i = 0; i < keys.Count; i += 25)
			{
				int f2 = i;
				int t2 = Math.Min(i + 25, keys.Count);
				l.Add(Menu("Fields " + (f2 + 1) + "–" + t2 + " / " + keys.Count, () => XFieldsMenu(keys, o, f2, t2, depth)));
			}
			return;
		}
		XArray a = v as XArray;
		if (a != null)
		{
			if (a.Items.Count <= 25)
			{
				for (int j = 0; j < a.Items.Count; j++)
				{
					AddXEntry(l, "[" + j + "]", a.Items[j], depth);
				}
				return;
			}
			for (int k = 0; k < a.Items.Count; k += 25)
			{
				int f = k;
				int t = Math.Min(k + 25, a.Items.Count);
				l.Add(Menu("Items " + (f + 1) + "–" + t + " / " + a.Items.Count, () => XItemsMenu(a, f, t, depth)));
			}
			return;
		}
		XPrim p = v as XPrim;
		if (p != null)
		{
			l.Add(Info("Value " + p.TypeName + "", () => p.Display));
		}
	}

	private List<Item> XFieldsMenu(List<string> keys, XObject o, int from, int to, int depth)
	{
		List<Item> list = new List<Item> { Head("FIELDS " + (from + 1) + "–" + to) };
		for (int i = from; i < to; i++)
		{
			AddXEntry(list, keys[i], o[keys[i]], depth);
		}
		return list;
	}

	private List<Item> XItemsMenu(XArray a, int from, int to, int depth)
	{
		List<Item> list = new List<Item> { Head("ITEMS " + (from + 1) + "–" + to) };
		for (int i = from; i < to; i++)
		{
			AddXEntry(list, "[" + i + "]", a.Items[i], depth);
		}
		return list;
	}

	private void AddXEntry(List<Item> l, string name, XValue v, int depth)
	{
		XValue v2 = v;
		string name2 = name;
		if (v2 is XObject || v2 is XArray)
		{
			l.Add(Menu(name2 + "" + v2.Display, () => XNodeMenu(v2, name2, depth + 1)));
		}
		else
		{
			l.Add(Info(name2 + " : " + v2.TypeName, () => v2.Display));
		}
	}

	private static string Num(float v, string fmt = "0")
	{
		return v.ToString(fmt, CultureInfo.InvariantCulture);
	}

	private static string MB(long bytes)
	{
		return ((double)bytes / 1048576.0).ToString("N0", CultureInfo.InvariantCulture) + " MB";
	}

	private List<Item> MainMenu()
	{
		return new List<Item>
		{
			Menu("World", WorldRootMenu),
			Menu("Vehicle", VehicleRootMenu),
			Menu("Debug", DebugRootMenu),
			Menu("Render", RenderRootMenu),
			Menu("Tools", ToolsRootMenu)
		};
	}

	private List<Item> WorldRootMenu()
	{
		return new List<Item>
		{
			Head("WORLD"),
			Menu("World / Island", WorldMenu),
			Menu("Island travel", TravelItems),
			Menu("Streaming", StreamingMenu),
			Menu("Weather & Time of Day", WeatherMenu),
			Menu("GPS / Bookmarks", GpsMenu),
			Menu("Wrecks / Detector", WrecksMenu),
			Menu("Cruising / Discovery", DiscoveryMenu),
			Menu("Audio", AudioMenu)
		};
	}

	private List<Item> VehicleRootMenu()
	{
		return new List<Item>
		{
			Head("VEHICLE"),
			Menu("Cars", CarsMenu),
			Menu("All cars", CarList),
			Menu("Own cars", OwnCarsMenu),
			Menu("Traffic", TrafficMenu),
			Menu("Traffic cars", TrafficCarListMenu),
			Menu("Physics settings", CarPhysicsMenu),
			Menu("Physics debug", CarDebugMenu),
			Menu("Avatar / clothes", AvatarMenu)
		};
	}

	private List<Item> DebugRootMenu()
	{
		return new List<Item>
		{
			Head("DEBUG VIEWS"),
			Menu("Debug views", DebugViewMenu),
			Menu("Collision view + colors", CollisionViewMenu),
			Menu("AI path system + colors", AiPathMenu),
			Menu("Camera / Free-fly", CameraDebugViewMenu),
			Head("DEBUG DATA"),
			Menu("Network traffic", NetworkTrafficMenu),
			Menu("Savegame debug", SavegameDebugMenu),
			Menu("UI / Flash debug", FlashMenu),
			Menu("Flash profiler / graph / log", FlashProfilerMenu)
		};
	}

	private List<Item> RenderRootMenu()
	{
		return new List<Item>
		{
			Head("RENDER"),
			Menu("Graphics / rendering", RenderingMenu),
			Menu("Render distance / terrain", RenderDistanceMenu),
			Menu("Performance", UsageMenu),
			Menu("Jobs & threads", JobsMenu)
		};
	}

	private List<Item> ToolsRootMenu()
	{
		return new List<Item>
		{
			Head("EDITORS"),
			Action_("Open Profiler", OpenProfiler),
			Action_("Scene Editor", OpenEditor),
			Action_("Flash Editor", delegate
			{
				FlashBrowser.Open();
				Toast("Flash Browser opened");
			}),
			Head("VIEWERS & RACES"),
			Menu("Cutscene Viewer", CutsceneMenu),
			Menu("Animation Viewer", AnimViewerMenu),
			Menu("Race file / start", RaceMenu),
			Head("PLAYER & SESSION"),
			Menu("Savegame / profile / money", SavegameMenu),
			Menu("Network / session", NetworkMenu),
			Menu("Players", PlayersMenu),
			Menu("Controls / input devices", ControlsMenu),
			Head("INPUT"),
			Bool("Toggle Control: drive with the menu open " + ToggleKeyName() + "", () => _drive, SetDrive, def: false),
			Bool("Input overlay (axes, pedals, buttons)", () => InputOverlayVisible, delegate(bool v)
			{
				InputOverlayVisible = v;
			}, def: false)
		};
	}

	private List<Item> CarsMenu()
	{
		VehicleManager j = Mgr;
		List<Item> list = new List<Item> { Head("CURRENT CAR") };
		if (j == null)
		{
			list.Add(Info("no vehicle manager"));
			return list;
		}
		list.Add(Info("Car", delegate
		{
			CarPhysics carPhysics = j.Current?.Car;
			return (carPhysics == null) ? "-" : ((carPhysics.Brand + "" + carPhysics.ModelName).Trim() + " [" + carPhysics.FileName + ", Id " + carPhysics.Id + "]");
		}));
		list.Add(Info("Speed", () => (j.Current == null) ? "-" : (Num(j.Current.SpeedKmh) + " km/h")));
		list.Add(Table("Driving aids", AidNames, () => (int)j.Aids, delegate(int i)
		{
			j.Aids = (DrivingAids)i;
			if (j.Current != null)
			{
				j.Current.Aids = j.Aids;
			}
		}, 1));
		list.Add(Bool("Automatic gearbox", () => j.AutomaticGearbox, delegate(bool v)
		{
			j.AutomaticGearbox = v;
			if (j.Current != null)
			{
				j.Current.AutomaticGearbox = v;
			}
		}, def: true));
		list.Add(Action_("Wash car", delegate
		{
			if (j.Dirt == null)
			{
				Toast("No dirt system on the car");
			}
			else
			{
				j.Dirt.Wash();
				Toast("Car washed");
			}
		}));
		list.Add(Action_("Right / reset car", delegate
		{
			if (j.Current != null)
			{
				j.ResetUpright();
				Toast("Car reset");
			}
		}));
		list.Add(Bool("Mirror reflections", () => j.MirrorReflections, delegate(bool v)
		{
			j.MirrorReflections = v;
		}, def: true));
		return list;
	}

	private List<Item> CarList()
	{
		VehicleManager i = Mgr;
		List<Item> list = new List<Item> { Head("ALL CARS") };
		if (i == null)
		{
			return list;
		}
		List<CarPhysics> list2 = new List<CarPhysics>(i.Available);
		list2.Sort((CarPhysics a, CarPhysics b) => string.Compare(a.Brand + a.ModelName, b.Brand + b.ModelName, StringComparison.OrdinalIgnoreCase));
		if (list2.Count == 0)
		{
			list.Add(Info("no cars loaded"));
			return list;
		}
		foreach (CarPhysics item in list2)
		{
			CarPhysics car = item;
			list.Add(new Item
			{
				Label = (car.Brand + "" + car.ModelName).Trim(),
				Value = () => (i.CurrentCar != car) ? car.FileName : "<- driving",
				Activate = delegate
				{
					DriveCar(car);
				}
			});
		}
		return list;
	}

	private void DriveCar(CarPhysics car)
	{
		VehicleManager mgr = Mgr;
		MapStreamer streamer = Streamer;
		if (mgr == null)
		{
			return;
		}
		int num = mgr.Available.IndexOf(car);
		int num2 = ((mgr.CurrentCar != null) ? mgr.Available.IndexOf(mgr.CurrentCar) : (-1));
		if (num >= 0)
		{
			if (mgr.Current != null && num2 >= 0)
			{
				mgr.SwitchCar(num - num2);
			}
			else if (streamer != null && _terrain.Camera != null)
			{
				mgr.SpawnCar(car.FileName, streamer.ViewerGame, _terrain.Camera.Forward);
			}
			Toast("Car: " + (car.Brand + "" + car.ModelName).Trim());
		}
	}

	private List<Item> WorldMenu()
	{
		MapStreamer s = Streamer;
		List<Item> list = new List<Item> { Head("STATUS") };
		if (s == null || s.Index == null)
		{
			list.Add(Info("no island loaded"));
			list.AddRange(TravelItems());
			return list;
		}
		list.Add(Info("Island", () => IslandTravel.DisplayName(s.Index?.IslandName) + "" + s.Status + ""));
		list.Add(Info("Position", () => Num(s.ViewerGame.X) + " / " + Num(s.ViewerGame.Y) + " / " + Num(s.ViewerGame.Z)));
		list.Add(Info("Sector", () => WorldLayout.SectorAt(s.ViewerGame.X, s.ViewerGame.Z).ToString()));
		list.Add(Info("Areas / sectors loaded", () => s.LoadedAreas + " / " + s.LoadedSectors));
		list.Add(Info("Objects drawn", () => s.DrawnObjects.ToString("N0", CultureInfo.InvariantCulture)));
		list.Add(Info("Instances", () => s.PlacementInstances.ToString("N0", CultureInfo.InvariantCulture)));
		list.Add(Info("Object library", () => (!s.LibraryReady) ? "loading" : "ready"));
		list.Add(Info("World-Actors", () => (!s.ActorsReady) ? "loading" : "ready"));
		list.Add(Info("Spots", () => (s.Spots == null) ? "-" : s.Spots.Spots.Count.ToString(CultureInfo.InvariantCulture)));
		list.Add(Info("Pending textures", () => s.PendingTextures.ToString(CultureInfo.InvariantCulture)));
		list.Add(Head("CONTENT"));
		list.Add(Bool("Island model", () => s.ShowIslandLod, delegate(bool v)
		{
			s.ShowIslandLod = v;
		}, def: true));
		list.Add(Bool("Area objects", () => s.LoadAreaObjects, delegate(bool v)
		{
			s.LoadAreaObjects = v;
		}, def: true));
		list.Add(Bool("Sector objects", () => s.LoadSectorObjects, delegate(bool v)
		{
			s.LoadSectorObjects = v;
		}, def: true));
		list.Add(Bool("Vegetation", () => s.LoadVegetation, delegate(bool v)
		{
			s.LoadVegetation = v;
		}, def: true));
		list.Add(Bool("Other placements", () => s.LoadOtherPlacements, delegate(bool v)
		{
			s.LoadOtherPlacements = v;
		}, def: true));
		list.Add(Bool("Actors", () => s.LoadActors, delegate(bool v)
		{
			s.LoadActors = v;
		}, def: true));
		list.Add(Bool("World actors", () => s.LoadWorldActors, delegate(bool v)
		{
			s.LoadWorldActors = v;
		}, def: true));
		list.Add(Bool("Grass", () => s.LoadGrass, delegate(bool v)
		{
			s.LoadGrass = v;
		}, def: true));
		list.Add(Bool("Spot buildings", () => s.LoadSpotExteriors, delegate(bool v)
		{
			s.LoadSpotExteriors = v;
		}, def: true));
		list.Add(Bool("Interiors", () => s.LoadInteriors, delegate(bool v)
		{
			s.LoadInteriors = v;
		}, def: true));
		list.Add(Bool("Open tunnel portals", () => s.CutTunnelPortals, delegate(bool v)
		{
			s.CutTunnelPortals = v;
		}, def: true));
		list.Add(Head("DISPLAY"));
		list.Add(Bool("Water", () => s.ShowWater, delegate(bool v)
		{
			s.ShowWater = v;
		}, def: true));
		list.Add(Bool("Clouds", () => s.ShowClouds, delegate(bool v)
		{
			s.ShowClouds = v;
		}, def: true));
		list.Add(Bool("Stars / moon", () => s.ShowStars, delegate(bool v)
		{
			s.ShowStars = v;
		}, def: true));
		list.Add(Bool("Power wires", () => s.ShowPowerWires, delegate(bool v)
		{
			s.ShowPowerWires = v;
		}, def: true));
		list.Add(Bool("Road sign text", () => s.ShowSignText, delegate(bool v)
		{
			s.ShowSignText = v;
		}, def: true));
		list.Add(Head("ACTIONS"));
		list.Add(Action_("Reload world", delegate
		{
			if (IslandTravel.Pending)
			{
				Toast("Island travel running");
			}
			else if (WorldWeather.Inside)
			{
				Toast("Leave the interior first");
			}
			else
			{
				IslandTravel.Go(s.Island, flight: false, Mgr?.CurrentCar?.FileName);
				Toast("Reloading " + IslandTravel.DisplayName(s.Island));
				Close();
			}
		}));
		list.Add(Action_("Clean memory", CleanMemory));
		return list;
	}

	private List<Item> StreamingMenu()
	{
		MapStreamer s = Streamer;
		List<Item> list = new List<Item> { Head("STATUS") };
		if (s == null || s.Index == null)
		{
			list.Add(Info("no island loaded"));
			return list;
		}
		list.Add(Info("Load jobs", () => s.PendingJobs + " / " + s.MaxParallelLoads));
		list.Add(Info("Collider jobs", () => s.ColliderJobs.ToString(CultureInfo.InvariantCulture)));
		list.Add(Info("Areas / sectors", () => s.LoadedAreas + " · " + s.LoadedSectors));
		list.Add(Head("LOADING"));
		list.Add(Int("Parallel load jobs", () => s.MaxParallelLoads, delegate(int v)
		{
			s.MaxParallelLoads = v;
		}, 1, 8, 4));
		list.Add(Float("Load areas up to", () => s.AreaLoadDistance, delegate(float v)
		{
			s.AreaLoadDistance = v;
		}, 100f, 500f, 10000f, new GraphicsConfig().TreeViewDistance, "0", " m"));
		list.Add(Float("Unload areas", () => s.AreaUnloadMargin, delegate(float v)
		{
			s.AreaUnloadMargin = v;
		}, 100f, 100f, 5000f, 1000f, "0", " m"));
		list.Add(Float("Load sectors up to", () => s.SectorLoadDistance, delegate(float v)
		{
			s.SectorLoadDistance = v;
		}, 50f, 100f, 10000f, new GraphicsConfig().TreeViewDistance, "0", " m"));
		list.Add(Float("Unload sectors", () => s.SectorUnloadMargin, delegate(float v)
		{
			s.SectorUnloadMargin = v;
		}, 50f, 50f, 3000f, 300f, "0", " m"));
		list.Add(Head("DETAIL"));
		list.Add(Bool("Cull last LOD", () => s.CullLastLod, delegate(bool v)
		{
			s.CullLastLod = v;
		}, def: false));
		list.Add(Float("Grass radius", () => s.GrassRadius, delegate(float v)
		{
			s.GrassRadius = v;
		}, 10f, 20f, 500f, 120f, "0", " m"));
		list.Add(Float("Grass density", () => s.GrassDensity, delegate(float v)
		{
			s.GrassDensity = v;
		}, 0.05f, 0f, 2f, 0.9f, "0.00"));
		list.Add(Float("Grass size", () => s.EffectiveGrassSizeScale, delegate(float v)
		{
			s.SetGrassSizeScale(v);
		}, 0.1f, 0.25f, 4f, s.EffectiveGrassSizeScale, "0.00", "x"));
		list.Add(Info("Grass size active", () => s.EffectiveGrassSizeScale.ToString("0.00", CultureInfo.InvariantCulture) + "x · " + (s.IsHawaii ? "Hawaii" : s.Island)));
		list.Add(Float("Grass ground mix", () => s.GrassGroundTipMix, delegate(float v)
		{
			s.SetGrassGroundTipMix(v);
		}, 0.05f, 0f, 1f, 0.3f, "0.00"));
		return list;
	}

	private List<Item> RenderDistanceMenu()
	{
		MapStreamer s = Streamer;
		List<Item> list = new List<Item> { Head("RENDER DISTANCE") };
		if (s == null || s.Index == null)
		{
			list.Add(Info("no island loaded"));
			return list;
		}
		list.Add(Table("Object render distance", RenderDistance.Names, () => (int)RenderDistance.Level, delegate(int i)
		{
			RenderDistance.Level = (RenderDistanceLevel)i;
		}, 2));
		list.Add(Info("Effective", () => "x" + Num(RenderDistance.Multiplier, "0.##") + " · vegetation " + Num(s.VegetationDistance * RenderDistance.Multiplier) + " m"));
		list.Add(Head("TERRAIN DISPLAY"));
		list.Add(Float("Lower terrain", () => s.TerrainLower, delegate(float v)
		{
			s.TerrainLower = v;
		}, 0.01f, 0f, 0.5f, 0.1f, "0.00", " m"));
		list.Add(Float("Terrain depth push", () => s.TerrainDepthPush * 1000f, delegate(float v)
		{
			s.TerrainDepthPush = v / 1000f;
		}, 0.1f, 0f, 10f, 1.5f, "0.0", " mm/m"));
		list.Add(Head("DETAIL"));
		list.Add(Float("Object LOD distance", () => s.LodDistanceScale, delegate(float v)
		{
			s.LodDistanceScale = v;
		}, 0.25f, 0.25f, 10f, 4f, "0.00", " x"));
		list.Add(Float("2D tree draw distance", () => s.VegetationDistance, delegate(float v)
		{
			s.VegetationDistance = v;
		}, 50f, 100f, 10000f, new GraphicsConfig().ImpostorViewDistance, "0", " m"));
		return list;
	}

	private static string HourText(float h)
	{
		h = (h % 24f + 24f) % 24f;
		int num = (int)MathF.Floor(h);
		int num2 = (int)MathF.Floor((h - (float)num) * 60f);
		return num.ToString("00", CultureInfo.InvariantCulture) + ":" + num2.ToString("00", CultureInfo.InvariantCulture);
	}

	private List<Item> WeatherMenu()
	{
		WorldWeather w = Weather;
		List<Item> list = new List<Item>
		{
			Head("CURRENT"),
			Info("Time", () => HourText(WorldWeather.Hour)),
			Info("Weather", () => KindNames[(int)(WorldWeather.Kind + 1)]),
			Info("Night / rain / wetness", () => Num(WorldWeather.Night * 100f) + " % / " + Num(WorldWeather.Rain * 100f) + " % / " + Num(WorldWeather.GroundWet * 100f) + " %")
		};
		if (w == null)
		{
			list.Add(Info("Weather not active"));
			return list;
		}
		list.Add(Head("CONTROL"));
		list.Add(new Item
		{
			Label = "Fixed time",
			Value = () => (!(w.ForceHour < 0f)) ? HourText(w.ForceHour) : "Automatic",
			Change = delegate(int d)
			{
				float num = ((w.ForceHour < 0f) ? (MathF.Floor(WorldWeather.Hour * 2f) / 2f) : w.ForceHour);
				num += 0.5f * (float)MathF.Sign(d) * ((Math.Abs(d) >= 10) ? 6f : 1f);
				w.ForceHour = (num % 24f + 24f) % 24f;
			},
			Reset = delegate
			{
				w.ForceHour = -1f;
			}
		});
		list.Add(Table("Fixed weather", KindNames, () => w.ForceKind + 1, delegate(int i)
		{
			w.ForceKind = i - 1;
		}, 0));
		list.Add(Float("Clock offset", () => w.HourOffset, delegate(float v)
		{
			w.HourOffset = v;
		}, 0.5f, -12f, 12f, 0f, "+0.0;-0.0;0", " h"));
		list.Add(Float("Length of a game day", () => w.DayMinutes, delegate(float v)
		{
			w.DayMinutes = v;
		}, 5f, 5f, 1440f, 15f, "0", " min"));
		list.Add(Float("Weather segment length", () => w.WeatherMinutes, delegate(float v)
		{
			w.WeatherMinutes = v;
		}, 1f, 1f, 120f, 4f, "0", " min"));
		list.Add(Float("Weather transition", () => w.TransitionSeconds, delegate(float v)
		{
			w.TransitionSeconds = v;
		}, 10f, 0f, 600f, 120f, "0", " s"));
		list.Add(Head("DISPLAY"));
		list.Add(Bool("Fog", () => w.ApplyFog, delegate(bool v)
		{
			w.ApplyFog = v;
		}, def: true));
		list.Add(Float("Fog strength", () => w.FogStrength, delegate(float v)
		{
			w.FogStrength = v;
		}, 0.05f, 0f, 1f, 0.05f, "0.00"));
		list.Add(Float("Sunlight", () => w.DiffuseScale, delegate(float v)
		{
			w.DiffuseScale = v;
		}, 0.05f, 0f, 2f, 0.65f, "0.00"));
		list.Add(Float("Ambient light", () => w.AmbientScale, delegate(float v)
		{
			w.AmbientScale = v;
		}, 0.05f, 0f, 2f, 0.75f, "0.00"));
		list.Add(PhBool("Auto headlights", def: true));
		list.Add(Int("Street lamps with light", () => w.LampLights, delegate(int v)
		{
			w.LampLights = v;
		}, 0, 32, 32));
		list.Add(Float("Lamp brightness", () => w.LampIntensity, delegate(float v)
		{
			w.LampIntensity = v;
		}, 0.1f, 0f, 2f, 1f, "0.0"));
		list.Add(Float("Street light on terrain", () => w.StreetLightStrength, delegate(float v)
		{
			w.StreetLightStrength = v;
		}, 0.05f, 0f, 1f, 0.5f, "0.00"));
		list.Add(Head("PRESETS"));
		list.Add(Action_("Sunrise", delegate
		{
			w.ForceHour = 6.5f;
			Toast("Time 6:30");
		}));
		list.Add(Action_("Noon", delegate
		{
			w.ForceHour = 12f;
			Toast("Time 12:00");
		}));
		list.Add(Action_("Dusk", delegate
		{
			w.ForceHour = 19.5f;
			Toast("Time 19:30");
		}));
		list.Add(Action_("Night", delegate
		{
			w.ForceHour = 23f;
			Toast("Time 23:00");
		}));
		list.Add(Action_("Midnight", delegate
		{
			w.ForceHour = 0f;
			Toast("Time 0:00");
		}));
		list.Add(Action_("Thunderstorm", delegate
		{
			w.ForceKind = 5;
			Toast("Weather: thunderstorm");
		}));
		list.Add(Action_("All automatic", delegate
		{
			w.ForceHour = -1f;
			w.ForceKind = -1;
			w.HourOffset = 0f;
			Toast("Weather/time synced again");
		}));
		return list;
	}

	private List<Item> RenderingMenu()
	{
		MapStreamer s = Streamer;
		List<Item> list = new List<Item> { Head("DISPLAY") };
		list.Add(Info("Resolution", () => _r.Width + " x " + _r.Height));
		list.Add(Info("Graphics", () => _r.DeviceName + "" + _r.Stats.Api + ", " + _r.Stats.Driver + ""));
		list.Add(Bool("VSync", () => GraphicsSettings.VSync, delegate(bool v)
		{
			GraphicsSettings.VSync = v;
		}, def: true));
		list.Add(Table("Anti-aliasing", MsaaNames, () => Math.Max(0, Array.IndexOf(MsaaValues, GraphicsSettings.Msaa)), delegate(int i)
		{
			GraphicsSettings.Msaa = MsaaValues[i];
		}, 0));
		list.Add(Head("UPSCALING (FSR)"));
		list.Add(Table("Upscaler", UpscalerNames, () => Math.Max(0, Array.IndexOf(UpscalerValues, (int)GraphicsSettings.Upscaler)), delegate(int i)
		{
			GraphicsSettings.Upscaler = (UpscalerMode)UpscalerValues[i];
		}, 0));
		list.Add(Table("Quality", UpscalerQualityNames, () => Math.Clamp((int)GraphicsSettings.UpscalerQuality, 0, UpscalerQualityNames.Length - 1), delegate(int i)
		{
			GraphicsSettings.UpscalerQuality = (UpscalerQuality)i;
		}, 0));
		list.Add(Float("Sharpness", () => GraphicsSettings.FsrSharpness, delegate(float v)
		{
			GraphicsSettings.FsrSharpness = v;
		}, 0.01f, 0f, 1f, 0.5f, "0.00"));
		list.Add(Info("Internal render size", () =>
		{
			float s = GraphicsSettings.RenderScale;
			return (int)(_r.Width * s) + " x " + (int)(_r.Height * s) + "  (" + Num(s, "0.00") + "x)";
		}));
		list.Add(Info("Renderer hook", () => _r.UpscalerStatus));
		list.Add(Float("View distance", () => _terrain.FarClip, delegate(float v)
		{
			_terrain.FarClip = v;
		}, 500f, 1000f, 60000f, 60000f, "0", " m"));
		list.Add(Head("ORIGINAL RENDERER"));
		list.Add(Bool("RenderUseOriginal", () => GraphicsSettings.RenderUseOriginal, delegate(bool v)
		{
			GraphicsSettings.RenderUseOriginal = v;
		}, def: false));
		list.Add(Action_("Translate original shaders", delegate
		{
			Task.Run((Action)OriginalShaders.SelfTest);
		}));
		list.Add(Info("Result", () => OriginalShaders.Status));
		list.Add(Info("Shader mapping", () => OriginalShaderSystem.Stats));
		list.Add(Action_("Map all materials", delegate
		{
			Task.Run((Action)OriginalShaderSystem.ScanAllMaterials);
		}));
		list.Add(Head("DETAIL"));
		list.Add(Table("Texture resolution", TexNames, () => Math.Clamp(GraphicsSettings.TextureMipLimit, 0, 3), delegate(int i)
		{
			GraphicsSettings.TextureMipLimit = i;
		}, 0));
		list.Add(Info("Object / tree draw distance", () => "Render > Render distance"));
		list.Add(Head("SHADOWS"));
		list.Add(Table("Shadow resolution", ShadowResNames, () => Math.Max(0, Array.IndexOf(ShadowResValues, GraphicsSettings.ShadowResolution)), delegate(int i)
		{
			GraphicsSettings.ShadowResolution = ShadowResValues[i];
		}, 1));
		list.Add(Table("Active cascades", ShadowCascadeNames, () => Math.Clamp(Array.IndexOf(ShadowCascadeValues, GraphicsSettings.ActiveShadowCascades), 0, 2), delegate(int i)
		{
			GraphicsSettings.ActiveShadowCascades = ShadowCascadeValues[i];
		}, 1));
		list.Add(Float("Shadow distance", () => GraphicsSettings.ShadowDistance, delegate(float v)
		{
			GraphicsSettings.ShadowDistance = v;
		}, 25f, 0f, 3000f, 0f, "0", " m"));
		list.Add(Head("WORLD"));
		if (s != null)
		{
			list.Add(Bool("Glow materials", () => s.ShowGlowMaterials, delegate(bool v)
			{
				s.ShowGlowMaterials = v;
				MaterialFactory.ShowGlowMaterials = v;
			}, def: true));
			list.Add(Float("Wind strength", () => s.WindStrength, delegate(float v)
			{
				s.WindStrength = v;
			}, 0.1f, 0f, 3f, 1f, "0.0"));
		}
		AddPostFxItems(list);
		return list;
	}

	private void CleanMemory()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		Toast("Cleaning memory");
	}

	private List<Item> UsageMenu()
	{
		RenderStats st = _r.Stats;
		List<Item> list = new List<Item>
		{
			Head("PROFILER"),
			Bool("GPU test: scene in 1/4 of the pixels", () => RenderFx.DiagQuarterPixels, delegate(bool v)
			{
				RenderFx.DiagQuarterPixels = v;
			}, def: false),
			Info(" -> compare 'GPU Scene' in the profiler: much faster = pixel bound, same = geometry bound"),
			Head("FRAME RATE"),
			Info("FPS", () => Num(_fps, "0.0")),
			Info("Frame time avg / min / max", () => Num(_ftAvg, "0.0") + " / " + Num(_ftMin, "0.0") + " / " + Num(_ftMax, "0.0") + " ms"),
			Head("RENDERING"),
			Info("Draw calls", () => st.DrawCalls.ToString("N0", CultureInfo.InvariantCulture)),
			Info("Pipeline binds", () => st.PipelineBinds.ToString("N0", CultureInfo.InvariantCulture)),
			Info("Triangles", () => st.Triangles.ToString("N0", CultureInfo.InvariantCulture)),
			Info("Transparent / shadow / overlay draws", () => st.TransparentDraws + " / " + st.ShadowDraws + " / " + st.OverlayDraws),
			Info("Textures / materials / meshes", () => st.Textures + " / " + st.Materials + " / " + st.Meshes)
		};
		if (GraphicsSettings.Upscaler != UpscalerMode.Off)
		{
			list.Add(Head("UPSCALING (FSR)"));
			list.Add(Info("Upscaler", () =>
			{
				int i = Array.IndexOf(UpscalerValues, (int)GraphicsSettings.Upscaler);
				return i >= 0 ? UpscalerNames[i] : GraphicsSettings.Upscaler.ToString();
			}));
			list.Add(Info("Quality", () => UpscalerQualityNames[Math.Clamp((int)GraphicsSettings.UpscalerQuality, 0, UpscalerQualityNames.Length - 1)]));
			list.Add(Info("Sharpness", () => Num(GraphicsSettings.FsrSharpness, "0.00")));
			list.Add(Info("Internal render size", () =>
			{
				float s = GraphicsSettings.RenderScale;
				return (int)(_r.Width * s) + " x " + (int)(_r.Height * s) + "  (" + Num(s, "0.00") + "x)";
			}));
			list.Add(Info("Renderer hook", () => _r.UpscalerStatus));
		}
		list.Add(Head("GPU LIVE"));
		list.AddRange(new List<Item>
		{
			Info("Source", () => (!_gpu.Available) ? "n/a" : _gpu.Source),
			Info("Core / memory clock", () => (!_gpu.Available) ? "-" : (_gpu.CoreClockMHz + " / " + _gpu.MemClockMHz + " MHz")),
			Info("GPU / memory load", () => (!_gpu.Available) ? "-" : (_gpu.GpuUtil + "% / " + (_gpu.MemUtilAvailable ? (_gpu.MemUtil + "%") : "-"))),
			Info("Temperature edge / hotspot", () => (!_gpu.Available) ? "-" : (_gpu.TempC + " / " + ((_gpu.TempHotspotC != 0) ? _gpu.TempHotspotC.ToString(CultureInfo.InvariantCulture) : "-") + " C")),
			Info("VRAM used / total", () => (!_gpu.Available) ? "-" : (MB((long)_gpu.VramUsed) + " / " + MB((long)_gpu.VramTotal))),
			Info("Board power", () => (!_gpu.Available || _gpu.PowerW == 0) ? "-" : (_gpu.PowerW + " W")),
			Head("MEMORY"),
			Info("GPU allocated", () => MB(st.GpuMemoryAllocated)),
			Info("VRAM usage / budget / total", () => MB(st.VramUsage) + " / " + MB(st.VramBudget) + " / " + MB(st.VramTotal)),
			Info(".NET heap", () => MB(GC.GetTotalMemory(forceFullCollection: false))),
			Info("Process working set", () => MB(Environment.WorkingSet)),
			Info("GC collections", () => GC.CollectionCount(0) + " / " + GC.CollectionCount(1) + " / " + GC.CollectionCount(2)),
			Head("SYSTEM"),
			Info("Graphics", () => st.DeviceName + "" + st.DeviceType + ""),
			Info("CPU threads", () => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
			Info("Uptime", () => TimeSpan.FromSeconds(_time).ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture)),
			Head("ACTIONS"),
			Action_("Clean memory", CleanMemory)
		});
		return list;
	}

	private List<Item> JobsMenu()
	{
		MapStreamer s = Streamer;
		List<Item> list = new List<Item> { Head("STREAMING") };
		if (s != null && s.Index != null)
		{
			list.Add(Info("Load jobs", () => s.PendingJobs + " / " + s.MaxParallelLoads));
			list.Add(Int("Parallel load jobs", () => s.MaxParallelLoads, delegate(int v)
			{
				s.MaxParallelLoads = v;
			}, 1, 8, 4));
			list.Add(Info("Collider jobs", () => s.ColliderJobs.ToString(CultureInfo.InvariantCulture)));
			list.Add(Float("Vegetation budget per frame", () => (float)s.VegetationBudgetMs, delegate(float v)
			{
				s.VegetationBudgetMs = v;
			}, 0.5f, 0.5f, 20f, 3f, "0.0", " ms"));
			list.Add(Float("Grass budget per frame", () => (float)s.GrassBudgetMs, delegate(float v)
			{
				s.GrassBudgetMs = v;
			}, 0.5f, 0.5f, 20f, 2f, "0.0", " ms"));
		}
		else
		{
			list.Add(Info("no island loaded"));
		}
		list.Add(Head("THREADPOOL"));
		list.Add(Info("Workers free / max", delegate
		{
			ThreadPool.GetAvailableThreads(out var workerThreads2, out var completionPortThreads3);
			ThreadPool.GetMaxThreads(out var workerThreads3, out completionPortThreads3);
			return workerThreads2 + " / " + workerThreads3 + "" + (workerThreads3 - workerThreads2) + "";
		}));
		list.Add(Info("I/O free / max", delegate
		{
			ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
			ThreadPool.GetMaxThreads(out workerThreads, out var completionPortThreads2);
			return completionPortThreads + " / " + completionPortThreads2;
		}));
		list.Add(Info("Threads", () => ThreadPool.ThreadCount + "" + ThreadPool.PendingWorkItemCount + ""));
		list.Add(Head("SYSTEM"));
		list.Add(Info("CPU threads", () => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)));
		return list;
	}

	private void OpenProfiler()
	{
		if (!Profiler.Available)
		{
			Toast("Profiler only in Debug builds");
			return;
		}
		Profiler.SetInfo("Engine", "Paradise Engine 0.1.2");
		ProfilerWindow.Open();
		Toast("Profiler opened");
	}

	private RaceDebugView ReadyRaceView()
	{
		RaceDebugView raceView = RaceView;
		if (!raceView.Loaded && GameData.BnkDir != null && _time >= _raceCatalogRefreshAt)
		{
			_raceCatalogRefreshAt = _time + 2.0;
			raceView.Refresh();
		}
		return raceView;
	}

	private void CollectRaceDebug()
	{
		RaceView.Collect(_terrain);
	}

	private List<Item> RaceMenu()
	{
		RaceDebugView v = ReadyRaceView();
		GAME_MODE_SCHOOL school = GAME_MODE_SCHOOL.Instance;
		List<Item> list = new List<Item>
		{
			Head("RACE CATALOG"),
			Info("Status", () => v.Status),
			Info("Selected", () => (v.Selected == null) ? "none" : v.Selected.Title),
			Info("Group", () => (v.Selected == null) ? "-" : v.SelectedGroupName),
			Info("Details", delegate
			{
				RaceDefinition selected2 = v.Selected;
				return (selected2 == null) ? "-" : (selected2.Checkpoints.Count + " CP · " + selected2.Decors.Count + " decor · " + selected2.Opponents.Count + " cars · " + selected2.Length.ToString("0") + " m · " + (selected2.IsCircuit ? "circuit" : "point-to-point") + " · " + selected2.Laps + " lap");
			}),
			Info("Times", delegate
			{
				RaceDefinition selected = v.Selected;
				return (selected == null) ? "-" : ("target " + ((selected.TargetTimeMs > 0) ? RaceHud.FormatRaceTimeMs(selected.TargetTimeMs) : "-") + (selected.HasTimeLimit ? (" · limit " + RaceHud.FormatRaceTimeMs(selected.TimeLimitMs)) : "") + " · car #" + selected.CarId);
			}),
			Info("Start", () => v.StartWorldText()),
			Info("Road route", () => (v.Selected?.RoadPath == null) ? "checkpoint line" : (v.Selected.RoadPath.Count + " points")),
			Info("Running", () => school.Race.State.ToString() + ((school.Race.Race != null) ? (" · " + school.Race.Race.Title + " · " + RaceHud.FormatRaceTime(school.Race.ElapsedSeconds) + " · CP " + school.Race.PassedCount + "/" + school.Race.TargetCount + ((school.Race.PenaltyMs > 0) ? (" · +" + RaceHud.FormatRaceTimeMs(school.Race.PenaltyMs)) : "")) : "")),
			Head("ACTIONS"),
			Action_("Start selected race", delegate
			{
				string text = v.StartRace();
				Toast(text);
				if (text.StartsWith("starting", StringComparison.OrdinalIgnoreCase))
				{
					Close();
				}
			}),
			Action_("Teleport player car to race start", delegate
			{
				Toast(v.TeleportCarToStart(_terrain, _vehicles));
			}),
			Action_("Move camera to race start", delegate
			{
				Toast(v.MoveCameraToStart(_terrain, freeCamera: true));
			}),
			Action_("Compute route along the roads", delegate
			{
				Toast(v.ComputeRoute(_vehicles));
			}),
			Action_("Abort running race", delegate
			{
				if (school.InTest)
				{
					school.Race.Abort();
					Toast("Race aborted");
				}
				else
				{
					Toast("No race running");
				}
			}),
			Action_("Select next race in this group", delegate
			{
				if (!v.CycleGroup(1))
				{
					Toast("No race selected / group empty");
				}
			}),
			Action_("Select previous race in this group", delegate
			{
				if (!v.CycleGroup(-1))
				{
					Toast("No race selected / group empty");
				}
			}),
			Action_("Refresh race catalog", delegate
			{
				v.Refresh();
				Toast("Race catalog refreshed");
			}),
			Head("DISPLAY"),
			Bool("Show race debug in the 3D world", () => v.ShowInWorld, delegate(bool x)
			{
				v.ShowInWorld = x;
			}, def: true),
			Bool("Runtime overlay + labels", () => v.ShowOverlay, delegate(bool x)
			{
				v.ShowOverlay = x;
			}, def: true),
			Bool("Route", () => v.ShowRoute, delegate(bool x)
			{
				v.ShowRoute = x;
			}, def: true),
			Bool("Follow roads", () => v.ShowRoadRoute, delegate(bool x)
			{
				v.ShowRoadRoute = x;
			}, def: true),
			Bool("Gates / checkpoints", () => v.ShowGates, delegate(bool x)
			{
				v.ShowGates = x;
			}, def: true),
			Bool("Cones / barriers", () => v.ShowDecor, delegate(bool x)
			{
				v.ShowDecor = x;
			}, def: true),
			Bool("Opponent cars", () => v.ShowOpponents, delegate(bool x)
			{
				v.ShowOpponents = x;
			}, def: true),
			Bool("World labels", () => v.ShowLabels, delegate(bool x)
			{
				v.ShowLabels = x;
			}, def: true),
			Float("View distance", () => v.ViewDistance, delegate(float x)
			{
				v.ViewDistance = x;
			}, 25f, 100f, 5000f, 900f, "0", " m"),
			Float("Label distance", () => v.LabelDistance, delegate(float x)
			{
				v.LabelDistance = x;
			}, 10f, 30f, 1500f, 250f, "0", " m"),
			Head("RACE / SCHOOL SETTINGS"),
			Bool("Gates", () => RaceConfig.ShowGates, delegate(bool x)
			{
				RaceConfig.ShowGates = x;
			}, def: true),
			Bool("Cones / barriers", () => RaceConfig.SpawnProps, delegate(bool x)
			{
				RaceConfig.SpawnProps = x;
			}, def: true),
			Float("Countdown", () => RaceConfig.CountdownSeconds, delegate(float x)
			{
				RaceConfig.CountdownSeconds = x;
			}, 0.1f, 0.5f, 10f, 3.7f, "0.0", " s"),
			Float("Penalty per hit", () => RaceConfig.PenaltySeconds, delegate(float x)
			{
				RaceConfig.PenaltySeconds = x;
			}, 1f, 0f, 60f, 10f, "0", " s"),
			Bool("Penalties also without PeLv", () => RaceConfig.PenaltiesAlways, delegate(bool x)
			{
				RaceConfig.PenaltiesAlways = x;
			}, def: false),
			Float("Penalty min. impact", () => RaceConfig.PenaltyMinImpact, delegate(float x)
			{
				RaceConfig.PenaltyMinImpact = x;
			}, 0.5f, 0f, 20f, 2.5f, "0.0", " m/s"),
			Bool("Unlock all licences", () => RaceConfig.UnlockAllLicences, delegate(bool x)
			{
				RaceConfig.UnlockAllLicences = x;
			}, def: false),
			Bool("Use the test car", () => RaceConfig.UseTestCar, delegate(bool x)
			{
				RaceConfig.UseTestCar = x;
			}, def: true),
			Bool("Back to the academy interior", () => RaceConfig.ReturnIntoInterior, delegate(bool x)
			{
				RaceConfig.ReturnIntoInterior = x;
			}, def: true),
			Bool("Hide players during tests", () => RaceConfig.HidePlayersInRacingSchool, delegate(bool x)
			{
				RaceConfig.HidePlayersInRacingSchool = x;
			}, def: true),
			Float("Autopilot cruise speed", () => RaceConfig.AutopilotCruiseKmh, delegate(float x)
			{
				RaceConfig.AutopilotCruiseKmh = x;
			}, 5f, 0f, 120f, 35f, "0", " km/h"),
			Action_("Reset racing school progress", delegate
			{
				RaceProgress.ResetAll(school.EnsureRepository()?.Database.Licenses);
				Toast("Racing school progress reset");
			})
		};
		if (!v.Loaded)
		{
			list.Add(Head(""));
			list.Add(Info("No catalog yet – waiting for the bnk folder."));
			return list;
		}
		list.Add(Head("RACES BY GROUP"));
		foreach (RaceGroup value in Enum.GetValues(typeof(RaceGroup)))
		{
			RaceGroup group = value;
			int count = v.FilesInGroup(group).Count;
			if (count != 0)
			{
				list.Add(Menu(RaceDebugView.GroupName(group) + "" + count + "", () => RaceList(group)));
			}
		}
		list.Add(Menu("ALL .ig2 FILES", AllRaceList));
		return list;
	}

	private List<Item> RaceList(RaceGroup group)
	{
		RaceDebugView raceDebugView = ReadyRaceView();
		List<Item> list = new List<Item> { Head(RaceDebugView.GroupName(group)) };
		List<string> list2 = raceDebugView.FilesInGroup(group);
		if (list2.Count == 0)
		{
			list.Add(Info("no races in this group"));
			return list;
		}
		foreach (string item in list2)
		{
			list.Add(RaceEntry(raceDebugView, item));
		}
		return list;
	}

	private List<Item> AllRaceList()
	{
		RaceDebugView raceDebugView = ReadyRaceView();
		List<Item> list = new List<Item> { Head("ALL .ig2 FILES") };
		if (raceDebugView.Files.Count == 0)
		{
			list.Add(Info("no files"));
			return list;
		}
		foreach (string file in raceDebugView.Files)
		{
			list.Add(RaceEntry(raceDebugView, file));
		}
		return list;
	}

	private Item RaceEntry(RaceDebugView v, string n)
	{
		RaceDebugView v2 = v;
		string i = n;
		string text = v2.HintFor(i);
		return Action_((string.Equals(i, v2.SelectedName, StringComparison.OrdinalIgnoreCase) ? "●" : "") + i.ToUpperInvariant() + ((text.Length > 0) ? (" [" + text + "]") : ""), delegate
		{
			if (v2.Select(i))
			{
				Toast("Selected " + i);
			}
			else
			{
				Toast(v2.Status);
			}
		});
	}

	private void AddRaceDebugToggles(List<Item> l)
	{
		RaceDebugView v = RaceView;
		l.Add(Bool("Show in the 3D world", () => v.ShowInWorld, delegate(bool x)
		{
			v.ShowInWorld = x;
		}, def: true));
		l.Add(Bool("Runtime overlay + labels", () => v.ShowOverlay, delegate(bool x)
		{
			v.ShowOverlay = x;
		}, def: true));
		l.Add(Bool("Route", () => v.ShowRoute, delegate(bool x)
		{
			v.ShowRoute = x;
		}, def: true));
		l.Add(Bool("Follow roads", () => v.ShowRoadRoute, delegate(bool x)
		{
			v.ShowRoadRoute = x;
		}, def: true));
		l.Add(Bool("Gates / checkpoints", () => v.ShowGates, delegate(bool x)
		{
			v.ShowGates = x;
		}, def: true));
		l.Add(Bool("Cones / barriers", () => v.ShowDecor, delegate(bool x)
		{
			v.ShowDecor = x;
		}, def: true));
		l.Add(Bool("Opponent cars", () => v.ShowOpponents, delegate(bool x)
		{
			v.ShowOpponents = x;
		}, def: true));
		l.Add(Bool("World labels", () => v.ShowLabels, delegate(bool x)
		{
			v.ShowLabels = x;
		}, def: true));
		l.Add(Float("View distance", () => v.ViewDistance, delegate(float x)
		{
			v.ViewDistance = x;
		}, 25f, 100f, 5000f, 900f, "0", " m"));
		l.Add(Action_("Move free camera to selected race", delegate
		{
			Toast(v.MoveCameraToStart(_terrain, freeCamera: true));
		}));
	}

	private void UpdateTools(float dt)
	{
		CutsceneTester.Instance?.Update(dt);
		AnimViewer.Instance?.Update(dt);
	}

	private void RenderTools()
	{
		CutsceneTester.Instance?.Render();
		AnimViewer.Instance?.Render();
	}

	private List<Item> CutsceneMenu()
	{
		VehicleManager j = Mgr;
		List<Item> list = new List<Item> { Head("CUTSCENE TESTER") };
		if (j == null || Streamer == null)
		{
			list.Add(Info("no vehicle manager / island loaded"));
			return list;
		}
		CutsceneTester t = Tester;
		list.Add(Info("Status", () => t.Status));
		list.Add(Info("Bank", () => (t.BankPath == null) ? "-" : DebugStage.Rel(j.BnkDir, t.BankPath)));
		list.Add(Table("Stage", StageNames3, () => (int)t.Where, delegate(int i)
		{
			t.Where = (CutsceneTester.Stage)i;
		}, 0));
		list.Add(Bool("Cast player avatar", () => t.CastAvatar, delegate(bool v)
		{
			t.CastAvatar = v;
			if (t.Scene != null)
			{
				t.Scene.CastPlayer = v;
			}
		}, def: true));
		list.Add(Bool("Cast player car", () => t.CastCar, delegate(bool v)
		{
			t.CastCar = v;
		}, def: true));
		list.Add(Bool("Auto-play first cinematic after loading", () => t.AutoPlay, delegate(bool v)
		{
			t.AutoPlay = v;
		}, def: true));
		list.Add(Bool("Show stage floor plane", () => t.ShowFloor, delegate(bool v)
		{
			t.ShowFloor = v;
		}, def: true));
		list.Add(Menu("Choose bank …", CutsceneBankMenu));
		if (t.Active)
		{
			list.Add(Head("CINEMATICS > Enter = PLAY · Left/Right = play as background · Delete = stop"));
			SpotCinematics scene = t.Scene;
			if (scene == null || !t.Loaded)
			{
				list.Add(Info("loading …"));
			}
			else
			{
				List<string> list2 = new List<string>(scene.Definitions.Keys);
				list2.Sort(StringComparer.OrdinalIgnoreCase);
				foreach (string item in list2)
				{
					string dn = item;
					SpotCinematics.Def d = scene.Definitions[dn];
					Vector3 gamePos;
					Quaternion gameRot;
					float fovDeg;
					bool flag = d.Cin.Camera(d.Scenes, 0f, out gamePos, out gameRot, out fovDeg);
					string flags = (flag ? "" : "no camera");
					list.Add(new Item
					{
						Label = "> " + dn,
						Value = () => ((t.Scene != null && t.Scene.MainName == dn) ? "PLAYING" : "[Enter]") + flags + d.Cin.Duration.ToString("0.0", CultureInfo.InvariantCulture) + " s",
						Activate = delegate
						{
							t.Play(dn, background: false);
						},
						Change = delegate
						{
							t.Play(dn, background: true);
						},
						Reset = delegate
						{
							t.StopAll();
						}
					});
				}
			}
			list.Add(Head("CONTROL"));
			list.Add(Float("Free view: orbit speed", () => t.ViewSpeed, delegate(float v)
			{
				t.ViewSpeed = v;
			}, 4f, -90f, 90f, 8f, "0", "°/s"));
			list.Add(Float("Free view: distance", () => t.ViewDistance, delegate(float v)
			{
				t.ViewDistance = v;
			}, 1f, 2f, 80f, 14f, "0", " m"));
			list.Add(Float("Free view: height", () => t.ViewHeight, delegate(float v)
			{
				t.ViewHeight = v;
			}, 0.5f, -5f, 40f, 5f, "0.0", " m"));
			list.Add(Action_("Skip main cinematic", delegate
			{
				t.Skip();
			}));
			list.Add(Action_("Stop all", delegate
			{
				t.StopAll();
			}));
			list.Add(Float("Time scale", () => t.TimeScale, delegate(float v)
			{
				t.TimeScale = v;
			}, 0.25f, 0f, 4f, 1f, "0.00", "x"));
			list.Add(Action_("Reload bank", delegate
			{
				string bankPath = t.BankPath;
				if (bankPath != null)
				{
					t.Load(bankPath);
				}
			}));
			list.Add(Action_("Close tester", delegate
			{
				t.TimeScale = 1f;
				t.Unload();
				Toast("Cutscene tester closed");
			}));
		}
		return list;
	}

	private List<Item> CutsceneBankMenu()
	{
		VehicleManager mgr = Mgr;
		List<Item> list = new List<Item> { Head("BANKS WITH CINEMATICS " + DebugStage.ScanState + "") };
		if (mgr == null)
		{
			return list;
		}
		List<string> list2 = DebugStage.CinematicBanks(mgr.BnkDir, Streamer);
		if (list2 == null)
		{
			list.Add(Info("scanning banks …"));
			return list;
		}
		list.Add(Action_("Rescan", delegate
		{
			DebugStage.Rescan();
			Rebuild();
		}));
		string text = null;
		foreach (string item in list2)
		{
			string rel = DebugStage.Rel(mgr.BnkDir, item);
			string text2 = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
			if (text2 != text)
			{
				text = text2;
				list.Add(Head(text2.ToUpperInvariant()));
			}
			string path = item;
			list.Add(Action_(Path.GetFileNameWithoutExtension(item), delegate
			{
				CutsceneTester tester = Tester;
				tester.Load(path);
				Toast("Loading " + rel + " – " + CutsceneTester.StageName(tester.Where));
				if (_stack.Count > 1)
				{
					_stack.RemoveAt(_stack.Count - 1);
				}
			}));
		}
		return list;
	}

	private List<Item> AnimViewerMenu()
	{
		VehicleManager i = Mgr;
		List<Item> list = new List<Item> { Head("ANIMATION VIEWER") };
		if (i == null || Streamer == null)
		{
			list.Add(Info("no vehicle manager / island loaded"));
			return list;
		}
		if (!AvatarLibrary.Ready)
		{
			list.Add(Info("avatar data not loaded yet"));
			return list;
		}
		AnimViewer v = Viewer;
		if (!v.Active)
		{
			list.Add(Action_("Open viewer", delegate
			{
				v.Open();
				Toast("Animation viewer open");
				Rebuild();
			}));
			return list;
		}
		if (_pnjIds == null)
		{
			_pnjIds = AvatarLibrary.PnjIds();
			_pnjIds.Insert(0, "");
		}
		List<string> ids = _pnjIds;
		list.Add(new Item
		{
			Label = "Model",
			Value = () => (!string.IsNullOrEmpty(v.Model)) ? (v.Model + "" + AvatarLibrary.PnjName(v.Model) + "") : "Player avatar",
			Change = delegate(int d)
			{
				int num = ids.IndexOf(v.Model ?? "");
				int count = ids.Count;
				num = ((num + d) % count + count) % count;
				v.SetModel(ids[num]);
			},
			Reset = delegate
			{
				v.SetModel("");
			}
		});
		list.Add(Menu("Choose model …", delegate
		{
			List<Item> list5 = new List<Item> { Head("MODELS") };
			foreach (string item in ids)
			{
				string i2 = item;
				list5.Add(Action_(string.IsNullOrEmpty(i2) ? "Player avatar" : (i2 + "" + AvatarLibrary.PnjName(i2)), delegate
				{
					v.SetModel(i2);
					if (_stack.Count > 1)
					{
						_stack.RemoveAt(_stack.Count - 1);
					}
				}));
			}
			return list5;
		}));
		list.Add(Info("Clip source", () => (!string.IsNullOrEmpty(v.ClipBank)) ? DebugStage.Rel(i.BnkDir, v.ClipBank) : "scenarics/avatars/avatarsdriver.bnk"));
		list.Add(Menu("Choose clip source …", delegate
		{
			List<Item> list3 = new List<Item> { Head("BANKS WITH ANIMATIONS " + DebugStage.ScanState + "") };
			list3.Add(Action_("avatarsdriver.bnk", delegate
			{
				v.SetBank("");
				if (_stack.Count > 1)
				{
					_stack.RemoveAt(_stack.Count - 1);
				}
			}));
			List<string> list4 = DebugStage.AnimationBanks(i.BnkDir, Streamer);
			if (list4 == null)
			{
				list3.Add(Info("scanning banks …"));
				return list3;
			}
			foreach (string item2 in list4)
			{
				string p = item2;
				list3.Add(Action_(DebugStage.Rel(i.BnkDir, item2), delegate
				{
					v.SetBank(p);
					if (_stack.Count > 1)
					{
						_stack.RemoveAt(_stack.Count - 1);
					}
				}));
			}
			return list3;
		}));
		list.Add(new Item
		{
			Label = "Clip",
			Value = () => (!string.IsNullOrEmpty(v.ClipName)) ? v.ClipName : "rest pose",
			Change = delegate(int d)
			{
				v.Step(Math.Sign(d));
			},
			Reset = delegate
			{
				v.SetClip("");
			}
		});
		list.Add(Menu("Choose clip …", delegate
		{
			List<Item> list2 = new List<Item> { Head("CLIPS") };
			foreach (string item3 in v.Clips())
			{
				string c2 = item3;
				list2.Add(new Item
				{
					Label = c2,
					Value = () => (!(v.ClipName == c2)) ? "" : ">",
					Activate = delegate
					{
						v.SetClip(c2);
					}
				});
			}
			if (list2.Count == 1)
			{
				list2.Add(Info("no clips in this bank"));
			}
			return list2;
		}));
		list.Add(Info("Time", () => (v.Clip == null) ? "-" : (v.Time_.ToString("0.00", CultureInfo.InvariantCulture) + " / " + v.Clip.Length.ToString("0.00", CultureInfo.InvariantCulture) + " s")));
		list.Add(Bool("Paused", () => v.Paused, delegate(bool x)
		{
			v.Paused = x;
		}, def: false));
		list.Add(Bool("Loop", () => v.Loop, delegate(bool x)
		{
			v.Loop = x;
		}, def: true));
		list.Add(Bool("Show floor plane", () => v.ShowFloor, delegate(bool x)
		{
			v.ShowFloor = x;
		}, def: true));
		list.Add(Float("Speed", () => v.Speed, delegate(float x)
		{
			v.Speed = x;
		}, 0.1f, -2f, 4f, 1f, "0.0", "x"));
		list.Add(Float("Scrub", () => v.Time_, delegate(float x)
		{
			v.Time_ = x;
			v.Paused = true;
		}, 0.05f, 0f, 600f, 0f, "0.00", " s"));
		list.Add(Head("CAMERA"));
		list.Add(Float("Orbit angle", () => v.Orbit, delegate(float x)
		{
			v.Orbit = x;
		}, 10f, -3600f, 3600f, 30f, "0", "°"));
		list.Add(Float("Orbit speed", () => v.OrbitSpeed, delegate(float x)
		{
			v.OrbitSpeed = x;
		}, 5f, -180f, 180f, 0f, "0", "°/s"));
		list.Add(Float("Distance", () => v.Distance, delegate(float x)
		{
			v.Distance = x;
		}, 0.2f, 0.5f, 20f, 3.2f, "0.0", " m"));
		list.Add(Float("Height", () => v.Height, delegate(float x)
		{
			v.Height = x;
		}, 0.1f, -1f, 3f, 1.1f, "0.0", " m"));
		list.Add(Action_("Close viewer", delegate
		{
			v.Close();
			Toast("Animation viewer closed");
			Rebuild();
		}));
		return list;
	}
}

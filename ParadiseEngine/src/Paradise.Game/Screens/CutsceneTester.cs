using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Online;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.Gamemodes;
using Paradise.Game.World;

namespace Paradise.Game.Screens;

internal sealed class CutsceneTester
{
	public enum Stage
	{
		Sky,
		Here,
		Spot
	}

	private static readonly string[] StageNames = new string[3] { "Sky stage", "Here", "At the bank's spot" };

	private static readonly Dictionary<string, (string spot, bool closed, float alt)> KnownSpots = new Dictionary<string, (string, bool, float)>(StringComparer.OrdinalIgnoreCase)
	{
		{
			"new_intro",
			("EPH_L3DI_5067I", false, 0f)
		},
		{
			"garage",
			("EPH_L3DI_5067I", true, 1500f)
		},
		{
			"meeting_tess",
			("E_HOTELA_1266I", false, 0f)
		},
		{
			"solar_crown_ext",
			("ECH_IA_1243I", false, 0f)
		},
		{
			"solar_crown_int",
			("ECH_IA_1243I", true, 0f)
		},
		{
			"pack_events13",
			("ECH_IA_1243I", false, 0f)
		}
	};

	private static CutsceneTester? _instance;

	private readonly IRenderer _r;

	private readonly TerrainModule _t;

	private readonly VehicleModule _v;

	public bool AutoPlay = true;

	public bool ShowFloor = true;

	public bool CastAvatar = true;

	public bool CastCar = true;

	public Stage Where;

	public float ViewAngle = 35f;

	public float ViewDistance = 14f;

	public float ViewHeight = 5f;

	public float ViewSpeed = 8f;

	public float TimeScale = 1f;

	private Task<SpotCinematics?>? _sceneTask;

	private Task<BankModel>? _modelTask;

	private MapStreamer.SceneHandle? _built;

	private Matrix4x4 _frame = Matrix4x4.Identity;

	private bool _staged;

	private bool _cast;

	private bool _skyFloor;

	private bool _props;

	private string? _spotCode;

	private string? _pending;

	private bool _pendingBg;

	public static CutsceneTester? Instance => _instance;

	public SpotCinematics? Scene { get; private set; }

	public bool Loaded
	{
		get
		{
			if (Scene != null && _sceneTask == null)
			{
				return _modelTask == null;
			}
			return false;
		}
	}

	public string? BankPath { get; private set; }

	public string Status { get; private set; } = "idle";


	public bool Active
	{
		get
		{
			if (Scene == null)
			{
				return _sceneTask != null;
			}
			return true;
		}
	}

	public static string StageName(Stage s)
	{
		return StageNames[(int)s];
	}

	public static CutsceneTester Get(IRenderer r, TerrainModule t, VehicleModule v)
	{
		return _instance ?? (_instance = new CutsceneTester(r, t, v));
	}

	private CutsceneTester(IRenderer r, TerrainModule t, VehicleModule v)
	{
		_r = r;
		_t = t;
		_v = v;
	}

	public void Load(string bankPath)
	{
		string bankPath2 = bankPath;
		Unload(restore: false);
		VehicleManager manager = _v.Manager;
		MapStreamer st = _t.Streamer;
		if (manager == null || st == null || string.IsNullOrEmpty(bankPath2))
		{
			return;
		}
		if (!_staged)
		{
			DebugStage.Enter(_v, _t);
			_staged = true;
		}
		BankPath = bankPath2;
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(bankPath2);
		Stage stage = Where;
		bool flag = false;
		Vector3 position = Vector3.Zero;
		float angle = 0f;
		bool flag2 = true;
		_spotCode = null;
		_props = false;
		if (stage == Stage.Spot)
		{
			Spot spot = ((st.Index?.ExteriorDir != null && bankPath2.Replace('\\', '/').Contains("/exterior/", StringComparison.OrdinalIgnoreCase)) ? NearestSpot(fileNameWithoutExtension) : null);
			if (spot != null)
			{
				position = spot.Position;
				angle = spot.Value1;
				flag2 = false;
				_props = true;
				_spotCode = spot.Code;
			}
			else
			{
				if (KnownSpots.TryGetValue(fileNameWithoutExtension, out (string, bool, float) value))
				{
					Spot spot2 = FindSpot(value.Item1);
					if (spot2 != null)
					{
						position = spot2.Position;
						angle = spot2.Value1;
						if (value.Item2)
						{
							position += Vector3.UnitY * value.Item3;
						}
						goto IL_0187;
					}
				}
				stage = Stage.Sky;
				flag = true;
			}
		}
		goto IL_0187;
		IL_0187:
		if (stage == Stage.Here)
		{
			FlyCamera camera = _t.Camera;
			if (camera != null)
			{
				Vector3 value2 = camera.Override?.forward ?? camera.Forward;
				value2.Y = 0f;
				value2 = ((value2.LengthSquared() < 0.0001f) ? Vector3.UnitZ : Vector3.Normalize(value2));
				Vector3 vector = st.RenderToGame(camera.Position) + value2 * 8f;
				if (st.TryGetGroundHeight(vector, out var y))
				{
					vector.Y = y;
				}
				position = vector;
				angle = MathF.Atan2(value2.X, value2.Z);
			}
		}
		if (stage == Stage.Sky)
		{
			position = new Vector3(0f, 1500f, 0f);
			angle = 0f;
		}
		_frame = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle)) * Matrix4x4.CreateTranslation(position);
		_skyFloor = stage == Stage.Sky;
		_cast = false;
		IRenderer r = _r;
		_sceneTask = Task.Run(() => SpotCinematics.Open(r, bankPath2));
		if (flag2)
		{
			_modelTask = Task.Run(() => st.LoadSceneModel(bankPath2));
		}
		Status = "loading " + fileNameWithoutExtension + "" + StageName(stage) + (flag ? " – no spot for this bank on this island" : "") + "";
	}

	private Spot? FindSpot(string code)
	{
		string code2 = code;
		return (_t.Streamer?.Spots?.Spots)?.Find((Spot s) => string.Equals(s.Code, code2, StringComparison.OrdinalIgnoreCase));
	}

	private Spot? NearestSpot(string code)
	{
		MapStreamer streamer = _t.Streamer;
		if (streamer?.Spots == null || _t.Camera == null)
		{
			return null;
		}
		Vector3 vector = streamer.RenderToGame(_t.Camera.Position);
		Spot result = null;
		float num = float.MaxValue;
		foreach (Spot spot in streamer.Spots.Spots)
		{
			if (string.Equals(spot.Code, code, StringComparison.OrdinalIgnoreCase))
			{
				float num2 = (spot.Position - vector).LengthSquared();
				if (num2 < num)
				{
					num = num2;
					result = spot;
				}
			}
		}
		return result;
	}

	public void Play(string name, bool background)
	{
		if (Active)
		{
			if (!Loaded)
			{
				_pending = name;
				_pendingBg = background;
				Status = "queued " + name;
			}
			else
			{
				bool flag = (background ? Scene.PlayBackground(name, loop: false) : Scene.PlayMain(name));
				Status = (flag ? ((background ? "background" : "playing") + name) : (name + " failed"));
			}
		}
	}

	public void StopAll()
	{
		if (Scene != null)
		{
			Scene.StopMain();
			Scene.StopBackground();
			Status = "stopped";
		}
	}

	public void Skip()
	{
		Scene?.SkipMain();
	}

	public void Update(float dt)
	{
		if (!Active)
		{
			return;
		}
		MapStreamer streamer = _t.Streamer;
		if (_sceneTask != null && _sceneTask.IsCompleted)
		{
			Task<SpotCinematics> sceneTask = _sceneTask;
			_sceneTask = null;
			Scene = ((sceneTask.Status == TaskStatus.RanToCompletion) ? sceneTask.Result : null);
			if (Scene == null)
			{
				Status = "failed: " + (sceneTask.Exception?.GetBaseException().Message ?? "bank not readable");
				Unload(restore: false);
				return;
			}
			Scene.Frame = _frame;
			Scene.Indoor = _skyFloor;
			Scene.CastPlayer = CastAvatar;
			if (CastCar)
			{
				Scene.LoadCar(_v.Manager, CarForScene());
			}
			if (_props && streamer != null && BankPath != null)
			{
				Scene.AttachProps(streamer, BankPath, _spotCode);
			}
		}
		if (_modelTask != null && _modelTask.IsCompleted)
		{
			Task<BankModel> modelTask = _modelTask;
			_modelTask = null;
			if (modelTask.Status == TaskStatus.RanToCompletion && modelTask.Result != null && streamer != null)
			{
				try
				{
					_built = streamer.BuildScene(modelTask.Result, _frame, interior: true);
					streamer.Scenes.Add(_built);
				}
				catch (Exception ex)
				{
					Log.Warn("Cutscene Tester: scene – " + ex.Message);
					modelTask.Result.Release();
				}
			}
			else
			{
				Log.Warn("Cutscene Tester: scene not loaded – " + modelTask.Exception?.GetBaseException().Message);
			}
		}
		if (Scene == null)
		{
			return;
		}
		if (Loaded && !_cast)
		{
			_cast = true;
			if (Status.StartsWith("loading", StringComparison.Ordinal))
			{
				Status = "ready – " + Scene.Definitions.Count + " cinematics ";
			}
			if (AutoPlay && _pending == null)
			{
				foreach (SpotCinematics.Def value in Scene.Definitions.Values)
				{
					if (value.Cin.Camera(value.Scenes, 0f, out var _, out var _, out var _))
					{
						_pending = value.Name;
						_pendingBg = false;
						break;
					}
				}
			}
		}
		if (Loaded && _pending != null)
		{
			string pending = _pending;
			_pending = null;
			Play(pending, _pendingBg);
		}
		Scene.Update(dt * Math.Max(0f, TimeScale));
		string mainName = Scene.MainName;
		if (mainName != null && Status.StartsWith("playing", StringComparison.Ordinal))
		{
			SpotCinematics.Def def = Scene.Find(mainName);
			if (def != null)
			{
				Status = $"playing {mainName}{Scene.MainTime:0.0} / {def.Cin.Duration:0.0} s";
			}
		}
		foreach (string item in Scene.TakeEndEvents())
		{
			Status = "ended " + item;
		}
		ApplyCamera(dt);
	}

	private CarPhysics? CarForScene()
	{
		VehicleManager manager = _v.Manager;
		if (manager == null)
		{
			return null;
		}
		CarPhysics carPhysics = null;
		SaveData save = TDU2Session.Save;
		if (save != null && !string.IsNullOrEmpty(save.currentCar))
		{
			carPhysics = manager.FindCar(save.currentCar);
		}
		CarPhysics carPhysics2 = carPhysics;
		if (carPhysics2 == null)
		{
			carPhysics2 = manager.FindCar(manager.StartCar);
			if (carPhysics2 == null)
			{
				if (manager.Available.Count <= 0)
				{
					return null;
				}
				carPhysics2 = manager.Available[0];
			}
		}
		return carPhysics2;
	}

	private void ApplyCamera(float dt)
	{
		FlyCamera camera = _t.Camera;
		MapStreamer streamer = _t.Streamer;
		if (camera != null && streamer != null && Scene != null)
		{
			_t.FlyCameraEnabled = false;
			if (Scene.Camera(out var eye, out var forward, out var up, out var fov))
			{
				camera.Position = streamer.GameToRender(eye);
				camera.Override = (forward, up);
				_t.FieldOfView = fov;
				_t.NearClip = 0.05f;
				return;
			}
			ViewAngle += ViewSpeed * dt;
			Vector3 vector = Vector3.Transform(Vector3.UnitY * 1.2f, _frame);
			Vector3 value = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, _frame));
			Vector3 vector2 = Vector3.Transform(value, Quaternion.CreateFromAxisAngle(Vector3.UnitY, ViewAngle * (float)Math.PI / 180f));
			Vector3 vector3 = vector + vector2 * ViewDistance + Vector3.UnitY * ViewHeight;
			camera.Position = streamer.GameToRender(vector3);
			Vector3 vector4 = Vector3.Normalize(vector - vector3);
			Vector3 vector5 = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, vector4));
			camera.Override = (vector4, Vector3.Normalize(Vector3.Cross(vector4, vector5)));
			_t.FieldOfView = 60f;
			_t.NearClip = 0.05f;
		}
	}

	public void Render()
	{
		MapStreamer streamer = _t.Streamer;
		if (streamer != null && Active)
		{
			if (_skyFloor && ShowFloor)
			{
				DebugStage.DrawFloor(_r, _frame, streamer.RootMatrix, 600f);
			}
			Scene?.Render(streamer.RootMatrix);
		}
	}

	public void Unload(bool restore = true)
	{
		Scene?.StopMain();
		Scene?.StopBackground();
		Scene?.Dispose();
		Scene = null;
		if (_sceneTask != null)
		{
			Task<SpotCinematics> sceneTask = _sceneTask;
			_sceneTask = null;
			sceneTask.ContinueWith(delegate(Task<SpotCinematics?> x)
			{
				if (x.Status == TaskStatus.RanToCompletion)
				{
					x.Result?.Dispose();
				}
			});
		}
		if (_modelTask != null)
		{
			Task<BankModel> modelTask = _modelTask;
			_modelTask = null;
			modelTask.ContinueWith(delegate(Task<BankModel> x)
			{
				if (x.Status == TaskStatus.RanToCompletion)
				{
					x.Result?.Release();
				}
			});
		}
		if (_built != null)
		{
			_t.Streamer?.DestroyScene(_built);
			_built = null;
		}
		BankPath = null;
		_pending = null;
		if (restore && _staged)
		{
			_staged = false;
			DebugStage.Exit();
			Status = "idle";
		}
	}
}

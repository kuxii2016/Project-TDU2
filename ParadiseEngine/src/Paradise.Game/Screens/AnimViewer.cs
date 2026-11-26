using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Paradise.Core.Avatars;
using Paradise.Core.Diagnostics;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.World;
using Paradise.Game.World;

namespace Paradise.Game.Screens;

internal sealed class AnimViewer
{
	private static AnimViewer? _instance;

	private readonly IRenderer _r;

	private readonly TerrainModule _t;

	private readonly VehicleModule _v;

	public float Time_;

	public float Speed = 1f;

	public float Orbit = 30f;

	public float Distance = 3.2f;

	public float Height = 1.1f;

	public float OrbitSpeed;

	public bool Paused;

	public bool Loop = true;

	public bool AutoPlay = true;

	public bool ShowFloor = true;

	private static readonly Vector3 StagePos = new Vector3(0f, 1600f, 0f);

	private AvatarModel? _avatar;

	private BnkArchive? _bank;

	private List<string>? _bankClips;

	public static AnimViewer? Instance => _instance;

	public string Model { get; private set; } = "";


	public string ClipBank { get; private set; } = "";


	public string ClipName { get; private set; } = "";


	public AnmClip? Clip { get; private set; }

	public bool Active { get; private set; }

	public static AnimViewer Get(IRenderer r, TerrainModule t, VehicleModule v)
	{
		return _instance ?? (_instance = new AnimViewer(r, t, v));
	}

	private AnimViewer(IRenderer r, TerrainModule t, VehicleModule v)
	{
		_r = r;
		_t = t;
		_v = v;
	}

	public void Open()
	{
		if (!Active && _t.Streamer != null)
		{
			DebugStage.Enter(_v, _t);
			Active = true;
			SetModel(Model);
		}
	}

	public void Close()
	{
		if (Active)
		{
			_avatar?.Dispose();
			_avatar = null;
			Active = false;
			DebugStage.Exit();
		}
	}

	public void SetModel(string? pnjId)
	{
		Model = pnjId ?? "";
		if (Active)
		{
			_avatar?.Dispose();
			_avatar = null;
			AvatarConfig avatarConfig = (string.IsNullOrEmpty(Model) ? AvatarConfigs.Local : AvatarLibrary.PnjConfig(Model));
			_avatar = AvatarModel.Create(_r, avatarConfig ?? AvatarConfig.Default());
		}
	}

	public void SetBank(string? bankPath)
	{
		ClipBank = bankPath ?? "";
		_bank = null;
		_bankClips = null;
		if (string.IsNullOrEmpty(ClipBank))
		{
			return;
		}
		try
		{
			_bank = BnkArchive.Open(ClipBank);
		}
		catch (Exception ex)
		{
			Log.Warn("Animation Viewer: " + ex.Message);
		}
	}

	public List<string> Clips()
	{
		if (string.IsNullOrEmpty(ClipBank))
		{
			return AvatarLibrary.ClipNames();
		}
		if (_bankClips != null)
		{
			return _bankClips;
		}
		_bankClips = new List<string>();
		if (_bank != null)
		{
			foreach (BnkEntry item in _bank.All(".anm"))
			{
				_bankClips.Add(Path.GetFileNameWithoutExtension(item.Name));
			}
		}
		_bankClips.Sort(StringComparer.OrdinalIgnoreCase);
		return _bankClips;
	}

	public void SetClip(string? name)
	{
		ClipName = name ?? "";
		Clip = (string.IsNullOrEmpty(ClipName) ? null : (string.IsNullOrEmpty(ClipBank) ? AvatarLibrary.Clip(ClipName) : CineScene.LoadClip(_bank, ClipName)));
		Time_ = 0f;
		Paused = !AutoPlay;
	}

	public void Step(int dir)
	{
		List<string> list = Clips();
		if (list.Count != 0)
		{
			int num = list.IndexOf(ClipName);
			num = ((num + dir) % list.Count + list.Count) % list.Count;
			SetClip(list[num]);
		}
	}

	public void Update(float dt)
	{
		if (!Active)
		{
			return;
		}
		if (Clip != null && !Paused)
		{
			Time_ += dt * Speed;
			float num = MathF.Max(0.01f, Clip.Length);
			if (Time_ > num)
			{
				Time_ = (Loop ? (Time_ % num) : num);
			}
			if (Time_ < 0f)
			{
				Time_ = (Loop ? (num + Time_ % num) : 0f);
			}
		}
		_avatar?.Update();
		Orbit += OrbitSpeed * dt;
		FlyCamera camera = _t.Camera;
		MapStreamer streamer = _t.Streamer;
		if (camera != null && streamer != null)
		{
			_t.FlyCameraEnabled = false;
			Vector3 vector = StagePos + Vector3.UnitY * Height;
			Vector3 vector2 = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Orbit * (float)Math.PI / 180f));
			camera.Position = streamer.GameToRender(vector + vector2 * Distance + Vector3.UnitY * 0.25f);
			Vector3 vector3 = Vector3.Normalize(-vector2 - Vector3.UnitY * 0.08f);
			Vector3 vector4 = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, vector3));
			camera.Override = (vector3, Vector3.Normalize(Vector3.Cross(vector3, vector4)));
			_t.FieldOfView = 50f;
			_t.NearClip = 0.05f;
		}
	}

	public void Render()
	{
		MapStreamer streamer = _t.Streamer;
		if (!Active || streamer == null)
		{
			return;
		}
		Matrix4x4 matrix4x = Matrix4x4.CreateTranslation(StagePos);
		if (ShowFloor)
		{
			DebugStage.DrawFloor(_r, matrix4x, streamer.RootMatrix, 60f);
		}
		AvatarModel avatar = _avatar;
		if (avatar != null && avatar.Ready && avatar.Rig != null)
		{
			avatar.Rig.BeginPose();
			if (Clip != null)
			{
				avatar.Rig.ApplyClip(Clip, Time_);
			}
			avatar.Draw(matrix4x * streamer.RootMatrix);
		}
	}
}

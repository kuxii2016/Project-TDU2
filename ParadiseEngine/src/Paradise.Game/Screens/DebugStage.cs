using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Paradise.Core.Files;
using Paradise.Core.Formats;
using Paradise.Core.Render;
using Paradise.Core.Vehicles;
using Paradise.Core.World;
using Paradise.Game.World;

namespace Paradise.Game.Screens;

internal static class DebugStage
{
	private static int _users;

	private static CarPhysics? _car;

	private static Vector3 _pos;

	private static Vector3 _fwd = Vector3.UnitZ;

	private static VehicleModule? _vehicles;

	private static TerrainModule? _terrain;

	private static IGpuMaterial? _floorMat;

	private static IRenderer? _floorOwner;

	private static List<string>? _cineBanks;

	private static List<string>? _animBanks;

	private static Task? _scan;

	private static string? _scanIsland;

	public static bool Active => _users > 0;

	public static string ScanState { get; private set; } = "";


	public static void Enter(VehicleModule vehicles, TerrainModule terrain)
	{
		if (_users++ > 0)
		{
			return;
		}
		_vehicles = vehicles;
		_terrain = terrain;
		VehicleManager manager = vehicles.Manager;
		_car = ((manager?.Current != null) ? manager.CurrentCar : null);
		if (manager?.Current != null)
		{
			_pos = manager.PositionGame;
			_fwd = Flat(manager.ForwardGame);
		}
		else
		{
			MapStreamer streamer = terrain.Streamer;
			if (streamer != null)
			{
				FlyCamera camera = terrain.Camera;
				if (camera != null)
				{
					_pos = streamer.RenderToGame(camera.Position);
					_fwd = Flat(camera.Forward);
				}
			}
		}
		manager?.Despawn();
		vehicles.Suspended = true;
		terrain.FlyCameraEnabled = false;
		GameMode.Push(Gamemode.GAME_MODE_CINE);
	}

	public static void Exit()
	{
		if (_users <= 0 || --_users > 0)
		{
			return;
		}
		VehicleModule vehicles = _vehicles;
		TerrainModule terrain = _terrain;
		_vehicles = null;
		_terrain = null;
		GameMode.Pop();
		if (terrain?.Camera != null)
		{
			terrain.Camera.Override = null;
			terrain.NearClip = 1f;
			terrain.FieldOfView = 60f;
		}
		if (vehicles == null)
		{
			return;
		}
		vehicles.Suspended = false;
		if (terrain != null)
		{
			terrain.FlyCameraEnabled = true;
		}
		VehicleManager manager = vehicles.Manager;
		if (_car != null && manager != null)
		{
			Vector3 pos = _pos;
			if (terrain?.Streamer != null && terrain.Streamer.TryGetGroundHeight(pos, out var y))
			{
				pos.Y = y + 0.6f;
			}
			if (terrain?.Camera != null && terrain.Streamer != null)
			{
				terrain.Camera.Position = terrain.Streamer.GameToRender(pos - _fwd * 7f + Vector3.UnitY * 2.5f);
			}
			manager.SpawnCar(_car.FileName, pos, _fwd);
		}
		_car = null;
	}

	private static Vector3 Flat(Vector3 v)
	{
		v.Y = 0f;
		if (!(v.LengthSquared() < 0.0001f))
		{
			return Vector3.Normalize(v);
		}
		return Vector3.UnitZ;
	}

	public unsafe static void DrawFloor(IRenderer r, Matrix4x4 frame, Matrix4x4 gameToRender, float size)
	{
		if (_floorMat == null || _floorOwner != r)
		{
			_floorMat = r.CreateMaterial(new MaterialDesc
			{
				Kind = MaterialKind.Particle,
				Name = "Debug Floor"
			});
			_floorOwner = r;
		}
		float num = size * 0.5f;
		Matrix4x4 matrix = Matrix4x4.CreateTranslation(0f, -0.01f, 0f) * frame * gameToRender;
		uint color = ObjectVertex.PackColor(107, 110, 115, byte.MaxValue);
		Span<ObjectVertex> span = stackalloc ObjectVertex[4];
		span[0] = new ObjectVertex
		{
			Position = Vector3.Transform(new Vector3(0f - num, 0f, 0f - num), matrix),
			Normal = Vector3.UnitY,
			Color = color
		};
		span[1] = new ObjectVertex
		{
			Position = Vector3.Transform(new Vector3(num, 0f, 0f - num), matrix),
			Normal = Vector3.UnitY,
			Color = color
		};
		span[2] = new ObjectVertex
		{
			Position = Vector3.Transform(new Vector3(num, 0f, num), matrix),
			Normal = Vector3.UnitY,
			Color = color
		};
		span[3] = new ObjectVertex
		{
			Position = Vector3.Transform(new Vector3(0f - num, 0f, num), matrix),
			Normal = Vector3.UnitY,
			Color = color
		};
		Span<uint> indices = stackalloc uint[6] { 0, 1, 2, 0, 2, 3 };
		r.DrawDynamic(span, indices, _floorMat, Matrix4x4.Identity);
	}

	public static List<string>? CinematicBanks(string? bnkDir, MapStreamer? st)
	{
		StartScan(bnkDir, st);
		if (_scan == null || !_scan.IsCompleted)
		{
			return null;
		}
		return _cineBanks;
	}

	public static List<string>? AnimationBanks(string? bnkDir, MapStreamer? st)
	{
		StartScan(bnkDir, st);
		if (_scan == null || !_scan.IsCompleted)
		{
			return null;
		}
		return _animBanks;
	}

	public static void Rescan()
	{
		_scan = null;
	}

	private static void StartScan(string? bnkDir, MapStreamer? st)
	{
		string text = st?.Index?.IslandName ?? "";
		if ((_scan != null && _scanIsland == text) || string.IsNullOrEmpty(bnkDir))
		{
			return;
		}
		_scanIsland = text;
		string bnk = bnkDir;
		string ext = st?.Index?.ExteriorDir;
		ScanState = "scanning …";
		_scan = Task.Run(delegate
		{
			List<string> list = new List<string>();
			List<string> list2 = new List<string>();
			List<string> list3 = new List<string>();
			try
			{
				string text2 = Path.Combine(bnk, "scenarics");
				if (GameFiles.DirectoryExists(text2))
				{
					list3.AddRange(GameFiles.GetFiles(text2, "*.bnk", SearchOption.AllDirectories));
				}
			}
			catch (Exception)
			{
			}
			try
			{
				if (ext != null && GameFiles.DirectoryExists(ext))
				{
					list3.AddRange(GameFiles.GetFiles(ext, "*.bnk"));
				}
			}
			catch (Exception)
			{
			}
			try
			{
				string text3 = Path.Combine(bnk, "interior");
				if (GameFiles.DirectoryExists(text3))
				{
					list3.AddRange(GameFiles.GetFiles(text3, "*.bnk"));
				}
			}
			catch (Exception)
			{
			}
			int num = 0;
			foreach (string item in list3)
			{
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item);
				if (!fileNameWithoutExtension.Contains("__") && !fileNameWithoutExtension.EndsWith("_low", StringComparison.OrdinalIgnoreCase))
				{
					try
					{
						BnkArchive bnkArchive = BnkArchive.Open(item);
						bool flag = false;
						bool flag2 = false;
						foreach (BnkEntry entry in bnkArchive.Entries)
						{
							if (entry.Extension == ".cin")
							{
								flag = true;
							}
							else if (entry.Extension == ".anm")
							{
								flag2 = true;
							}
							if (flag && flag2)
							{
								break;
							}
						}
						if (flag)
						{
							list.Add(item);
						}
						if (flag2)
						{
							list2.Add(item);
						}
					}
					catch (Exception)
					{
					}
					num++;
				}
			}
			Comparison<string> comparison = (string a, string b2) => string.Compare(Rel(bnk, a), Rel(bnk, b2), StringComparison.OrdinalIgnoreCase);
			list.Sort(comparison);
			list2.Sort(comparison);
			_cineBanks = list;
			_animBanks = list2;
			ScanState = $"{list.Count} cinematic banks, {list2.Count} animation banks {num} scanned";
		});
	}

	public static string Rel(string? bnkDir, string? path)
	{
		if (string.IsNullOrEmpty(bnkDir) || path == null)
		{
			return path ?? "";
		}
		string text = path.Replace('\\', '/');
		string text2 = bnkDir.Replace('\\', '/').TrimEnd('/') + "/";
		if (!text.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		return text.Substring(text2.Length);
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Bluecadet.Launchpad;
using Bluecadet.Utils;
using UnityEngine;

namespace LaunchpadDemo
{
	/// <summary>
	/// Composition root: the one place where Utils (environment + settings)
	/// and Launchpad (feed + store + cache + gate + manager) are constructed
	/// and wired together. Everything below it depends only on the pieces it
	/// is handed, so this is also the only file that knows the whole graph.
	/// </summary>
	public sealed class AppBootstrap : MonoBehaviour
	{
		private SettingsFile<AppConfig> _settings;
		private LaunchpadClient _client;
		private ContentManager<Record> _content;
		private TextureCache _textures;
		private IdleGate _gate;
		private IdleTimeout _idle;
		private CancellationTokenSource _cts;

		// Typed views rebuilt from _content.Current on every OnVersionApplied,
		// per "Multiple content models" in the com.bluecadet.launchpad
		// README — the diff runs against one flat Record list, and these are
		// the ergonomic per-model lists a view actually wants.
		private List<Slide> _slides = new List<Slide>();
		private List<Sponsor> _sponsors = new List<Sponsor>();
		private ShowConfig _showConfig;

		private void Awake()
		{
			// 1. Environment first: DataPath honors --assetsPath, MachineId
			//    honors --machineId, so a kiosk install can relocate all
			//    settings + content outside the build without code changes.
			AppEnvironment env = AppEnvironment.Current;

			// 2. Settings via the tiered cascade (settings.json →
			//    settings.<machineId>.json → settings.local.json → --set).
			//    Keep the SettingsFile around — the Bluecadet Project Settings
			//    pane and TierFor() can then tell you where a value came from.
			_settings = env.SettingsFile<AppConfig>();
			AppConfig cfg = _settings.Value;

			// 3. Launchpad graph. contentRoot is relative in settings and
			//    resolved against DataPath here — same relocation story as
			//    the settings files themselves. T is Record, the shared base
			//    type for every model this version carries (slides, sponsors,
			//    and the "config" singleton), mapped by one DemoContentMapper.
			_client = new LaunchpadClient(cfg.controllerUrl, cfg.consumerId);
			var store = new ContentStore<Record>(
				env.ResolvePath(cfg.contentRoot), cfg.sourceFolders, new DemoContentMapper());
			_textures = new TextureCache();
			_gate = new IdleGate(TimeSpan.FromSeconds(cfg.maxSwapDeferSeconds));

			// Only Slide records carry media to preload; sponsors and the
			// config singleton are text-only.
			_content = new ContentManager<Record>(_client, store, _textures, _gate,
				record => record is Slide slide && !string.IsNullOrEmpty(slide.imagePath)
					? new[] { slide.imagePath }
					: Array.Empty<string>());

			_content.OnVersionStaged += v =>
				Debug.Log($"[Demo] Staged {v.VersionId}: +{v.Diff.Added.Count} ~{v.Diff.Changed.Count} -{v.Diff.RemovedIds.Count}");
			_content.OnVersionApplied += v =>
			{
				// Regrouped here, after the diff has already run against the
				// flat list, rather than kept as separate managers — see
				// "Why one flat list of records, not one bundle item" in the
				// README.
				_slides = v.Items.Select(i => i.Data).OfType<Slide>().ToList();
				_sponsors = v.Items.Select(i => i.Data).OfType<Sponsor>().ToList();
				_showConfig = v.Items.Select(i => i.Data).OfType<ShowConfig>().Single();
				Debug.Log($"[Demo] Applied {v.VersionId} ({v.Items.Count} records)");
			};

			// 4. Idle policy: Utils' IdleTimeout drives Launchpad's IdleGate,
			//    so staged content only swaps in while nobody is interacting
			//    (or after maxSwapDeferSeconds as a backstop).
			_idle = gameObject.AddComponent<IdleTimeout>();
			_idle.IdleTimeoutIntervals.Add(cfg.idleAfterSeconds);
			_idle.OnIdleState += _ => _gate.SetSwappable(true);

			// 5. Start. ContentManager cold-boots from whatever is already on
			//    disk, so the app works with the controller unreachable.
			_cts = new CancellationTokenSource();
			_client.Start(_cts.Token);
			_content.Start(_cts.Token);
		}

		private void Update()
		{
			// IdleTimeout doesn't hook input itself; the app decides what
			// counts as activity. Any activity also closes the swap gate.
			if (Input.anyKeyDown || Input.GetMouseButtonDown(0))
			{
				_idle.OnUserActivity();
				_gate.SetSwappable(false);
			}

			_content.TickMainThread();
		}

		private void OnDestroy()
		{
			_cts.Cancel();
			_content.Dispose();
			_client.Dispose();
			_textures.Dispose();
			_cts.Dispose();
		}

		// Diagnostic HUD only — a real app replaces this with its view layer,
		// subscribing to OnVersionApplied and reading the typed views above.
		private void OnGUI()
		{
			AppConfig cfg = _settings.Value;

			GUILayout.BeginArea(new Rect(16, 16, Screen.width - 32, Screen.height - 32));
			GUILayout.Label(_showConfig != null
				? $"{_showConfig.title}  (accent {_showConfig.accentColor}, {_showConfig.slideDurationSeconds}s/slide)"
				: "(no config yet)");
			GUILayout.Label($"Controller  {cfg.controllerUrl}  [{_client.State}]");
			GUILayout.Label($"Settings    {string.Join("  →  ", _settings.LoadedPaths)}");
			GUILayout.Label($"Content     {_content.State}  current={_content.CurrentVersionId ?? "-"}  staged={_content.StagedVersionId ?? "-"}  textures={_textures.Count}");

			if (_content.StagedVersionId != null && GUILayout.Button("Apply staged now", GUILayout.Width(160)))
			{
				_content.ApplyStagedNow();
			}

			GUILayout.Space(12);
			GUILayout.Label("Slides");
			foreach (Slide slide in _slides)
			{
				GUILayout.Label($"•  {slide.title} — {slide.body}");
			}

			GUILayout.Space(12);
			GUILayout.Label("Sponsors");
			foreach (Sponsor sponsor in _sponsors)
			{
				GUILayout.Label($"•  {sponsor.name} ({sponsor.tier}) — {sponsor.url}");
			}

			GUILayout.EndArea();
		}
	}
}

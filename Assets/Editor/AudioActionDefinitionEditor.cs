using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(AudioActionDefinition))]
public sealed class AudioActionDefinitionEditor : Editor
{
    private static readonly Color[] NodeColors =
    {
        new Color(0.9f, 0.72f, 0.2f),
        new Color(0.95f, 0.36f, 0.22f),
        new Color(0.75f, 0.2f, 0.9f),
        new Color(0.2f, 0.75f, 0.9f),
        new Color(0.2f, 0.85f, 0.35f)
    };

    private static readonly Color HeadColor = new Color(0.9f, 0.72f, 0.2f);
    private static readonly Color FirstPassColor = new Color(0.2f, 0.75f, 0.9f);
    private static readonly Color LoopColor = new Color(0.75f, 0.2f, 0.9f);
    private static readonly Color ReleaseTargetColor = new Color(0.2f, 0.85f, 0.35f);

    private static readonly string[] LegendLabels = { "裁掉", "启动", "保持段循环", "尾音" };
    private static readonly Color[] LegendColors = { HeadColor, FirstPassColor, LoopColor, ReleaseTargetColor };

    private bool _previewHeld;
    private double _previewStartTime;
    private float _previewSpeed;
    private float _previewContact;
    private string _previewStatus = string.Empty;
    private bool _previewUpdateBound;
    private AudioClip _analysisClip;
    private AudioActionClipAnalysis _analysis;
    private string _hoverHint = string.Empty;
    private int _draggingRegionHandle = -1;
    private string _correlationResult = string.Empty;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();
        serializedObject.ApplyModifiedProperties();

        AudioActionDefinition definition = (AudioActionDefinition)target;
        definition.EnsureDefaults();
        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("包络", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("恢复默认值", GUILayout.Width(92f)))
        {
            if (EditorUtility.DisplayDialog(
                "恢复默认值",
                "把这条定义的曲线、采样区域、ADSR、随机范围全部恢复为默认值？\nClip 和 Id 会保留。",
                "恢复", "取消"))
            {
                Undo.RecordObject(definition, "恢复音频定义默认值");
                definition.ResetToDefaults();
                EditorUtility.SetDirty(definition);
                GUI.changed = true;
                Repaint();
            }
        }
        if (GUILayout.Button("保存资产", GUILayout.Width(72f)))
        {
            // ScriptableObject 改字段不会自动落盘，这里手动写一次（等同于 Ctrl+S 存这个资产）。
            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssetIfDirty(definition);
            Repaint();
        }
        EditorGUILayout.EndHorizontal();
        if (GUILayout.Button("Wwise 绑定 / Event / 源 WAV"))
        {
            WwiseActionBindingWindow.Open(definition);
        }
        DrawAdsrPanel(definition);

        EditorGUILayout.Space(6f);
        DrawRegionPanel(definition);
        DrawAudition(definition);
    }

    private void OnDisable()
    {
        if (_previewUpdateBound) EditorApplication.update -= OnPreviewUpdate;
        _previewUpdateBound = false;
        WwiseActionPreviewPlayer.Stop();
    }

    private void OnPreviewUpdate()
    {
        if (_previewHeld || WwiseActionPreviewPlayer.IsActive) Repaint();
    }

    // ---------------- ADSR ----------------

    private static float SustainDisplayWidth(AdsrEnvelope envelope)
    {
        float span = envelope.AttackSeconds + envelope.DecaySeconds;
        return Mathf.Max(0.02f, span * 0.35f);
    }

    /// <summary>尾音区间长度（秒）：从 release 起点到素材结尾。release 时长就用它。</summary>
    private static float TailSeconds(AudioActionDefinition definition)
    {
        if (definition == null || definition.Clip == null) return 0.3f;
        float tailStart01 = definition.SeekToReleaseRegion
            ? Mathf.Clamp01(definition.ReleasePosition01)
            : Mathf.Clamp01(definition.LoopEnd01);
        return Mathf.Max(0.01f, (1f - tailStart01) * definition.Clip.length);
    }

    private static float AdsrValueAt(AdsrEnvelope e, float t, float sustainDisplay, float releaseSeconds)
    {
        float a = e.AttackSeconds, d = e.DecaySeconds, r = releaseSeconds;
        if (a > 0f && t < a)
            return Mathf.LerpUnclamped(0f, 1f, AudioCurveUtility.Map(t / a, e.AttackCurve));
        float decayTime = t - a;
        if (d > 0f && decayTime < d)
            return Mathf.LerpUnclamped(1f, e.SustainLevel,
                AudioCurveUtility.Map(decayTime / d, e.DecayCurve));
        float sustainTime = decayTime - d;
        if (sustainTime < sustainDisplay) return e.SustainLevel;
        float releaseTime = sustainTime - sustainDisplay;
        if (r > 0f)
            return Mathf.LerpUnclamped(e.SustainLevel, 0f,
                AudioCurveUtility.Map(releaseTime / r, e.ReleaseCurve));
        return 0f;
    }

    private void DrawAdsrPanel(AudioActionDefinition definition)
    {
        AdsrEnvelope envelope = definition.Envelope;
        if (envelope == null) return;
        Rect outer = GUILayoutUtility.GetRect(10f, 190f, GUILayout.ExpandWidth(true));
        DrawPanel(outer);
        Rect graph = new Rect(outer.x + 14f, outer.y + 16f, outer.width - 28f, outer.height - 58f);
        DrawGrid(graph);

        float sustainDisplay = SustainDisplayWidth(envelope);
        float releaseSeconds = TailSeconds(definition);
        float axis = Mathf.Max(0.05f, envelope.AttackSeconds + envelope.DecaySeconds
                                        + sustainDisplay + releaseSeconds);

        const int sampleCount = 140;
        var points = new Vector3[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1) * axis;
            float value = Mathf.Clamp01(AdsrValueAt(envelope, t, sustainDisplay, releaseSeconds));
            points[i] = new Vector3(
                Mathf.Lerp(graph.x, graph.xMax, t / axis),
                Mathf.Lerp(graph.yMax, graph.y, value),
                0f);
        }

        Handles.BeginGUI();
        Handles.DrawAAPolyLine(3.5f, points);
        DrawAdsrNode(graph, axis, 0f, 0f, NodeColors[0]);
        DrawAdsrNode(graph, axis, envelope.AttackSeconds, 1f, NodeColors[1]);
        DrawAdsrNode(graph, axis, envelope.AttackSeconds + envelope.DecaySeconds,
            envelope.SustainLevel, NodeColors[2]);
        DrawAdsrNode(graph, axis, envelope.AttackSeconds + envelope.DecaySeconds + sustainDisplay,
            envelope.SustainLevel, NodeColors[3]);
        DrawAdsrNode(graph, axis, envelope.AttackSeconds + envelope.DecaySeconds
            + sustainDisplay + releaseSeconds, 0f, NodeColors[4]);
        Handles.color = Color.white;
        Handles.EndGUI();

        _hoverHint = BuildAdsrHoverHint(envelope, graph, axis, sustainDisplay, releaseSeconds);

        GUI.Label(new Rect(outer.x + 12f, outer.y + outer.height - 40f, outer.width - 24f, 18f),
            $"A {envelope.AttackSeconds * 1000f:0} ms   D {envelope.DecaySeconds * 1000f:0} ms   " +
            $"S {envelope.SustainLevel * 100f:0}%   R {releaseSeconds * 1000f:0} ms（尾音区）",
            EditorStyles.miniLabel);
        GUI.Label(new Rect(outer.x + 12f, outer.y + outer.height - 22f, outer.width - 24f, 18f),
            string.IsNullOrEmpty(_hoverHint)
                ? "A/D/R 是时间，S 是电平。鼠标悬停到节点上看对应参数。"
                : _hoverHint,
            EditorStyles.miniLabel);
    }

    private static string BuildAdsrHoverHint(
        AdsrEnvelope envelope, Rect graph, float axis, float sustainDisplay, float releaseSeconds)
    {
        Event e = Event.current;
        if (e.type != EventType.Repaint || !graph.Contains(e.mousePosition)) return string.Empty;

        float attackX = Mathf.Lerp(graph.x, graph.xMax, Mathf.Clamp01(envelope.AttackSeconds / axis));
        float decayX = Mathf.Lerp(graph.x, graph.xMax,
            Mathf.Clamp01((envelope.AttackSeconds + envelope.DecaySeconds) / axis));
        float releaseX = Mathf.Lerp(graph.x, graph.xMax,
            Mathf.Clamp01((envelope.AttackSeconds + envelope.DecaySeconds + sustainDisplay) / axis));
        float endX = Mathf.Lerp(graph.x, graph.xMax,
            Mathf.Clamp01((envelope.AttackSeconds + envelope.DecaySeconds + sustainDisplay
                           + releaseSeconds) / axis));

        float x = e.mousePosition.x;
        if (Mathf.Abs(x - attackX) <= 10f)
            return $"Attack = {envelope.AttackSeconds * 1000f:0} ms（按下后爬升到满音量的时间）";
        if (Mathf.Abs(x - decayX) <= 10f)
            return $"Decay = {envelope.DecaySeconds * 1000f:0} ms，Sustain = {envelope.SustainLevel * 100f:0}%";
        if (Mathf.Abs(x - releaseX) <= 10f)
            return $"Release = {releaseSeconds * 1000f:0} ms（= 尾音区间长度，松手后播完这段）";
        if (Mathf.Abs(x - endX) <= 10f)
            return "Release 结束点 = 素材结尾";
        if (x > attackX && x < decayX)
            return "Decay 段：从满音量降到 SustainLevel";
        if (x > decayX && x < releaseX)
            return $"Sustain 平台：一直保持 {envelope.SustainLevel * 100f:0}%，长度取决于按住多久";
        if (x > releaseX)
            return "Release 段：松手后从这里降到 0";
        return "Attack 段：0 → 满音量";
    }

    private static void DrawAdsrNode(Rect graph, float total, float t, float value, Color color)
    {
        float x = Mathf.Lerp(graph.x, graph.xMax, Mathf.Clamp01(t / total));
        float y = Mathf.Lerp(graph.yMax, graph.y, Mathf.Clamp01(value));
        Handles.color = color;
        Handles.DrawSolidDisc(new Vector3(x, y, 0f), Vector3.forward, 5f);
        Handles.color = Color.white;
        Handles.DrawWireDisc(new Vector3(x, y, 0f), Vector3.forward, 5f);
    }

    // ---------------- 采样区域 ----------------

    private void DrawRegionPanel(AudioActionDefinition definition)
    {
        EditorGUILayout.LabelField("采样回放区域", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("分析素材能量", GUILayout.Width(120f)))
        {
            RefreshAnalysis(definition, true);
        }
        using (new EditorGUI.DisabledScope(_analysis == null || !_analysis.Success))
        {
            if (GUILayout.Button("自动设置循环区", GUILayout.Width(120f)))
            {
                ApplySuggestedLoop(definition);
            }
        }
        using (new EditorGUI.DisabledScope(_analysis == null || !_analysis.Success))
        {
            if (GUILayout.Button("互相关找循环点", GUILayout.Width(130f)))
            {
                ApplyCorrelationLoop(definition);
            }
        }
        if (_analysis != null && _analysis.Success)
        {
            EditorGUILayout.LabelField(
                $"有声 {_analysis.FirstAudible01:0.00}..{_analysis.LastAudible01:0.00} · 峰值 {_analysis.Peak01:0.00}",
                EditorStyles.miniLabel);
        }
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(_correlationResult))
        {
            EditorGUILayout.LabelField(_correlationResult, EditorStyles.miniLabel);
        }

        RefreshAnalysis(definition, false);

        float start = Mathf.Clamp01(definition.StartPosition01);
        float loopStart = Mathf.Clamp01(definition.LoopStart01);
        float loopEnd = Mathf.Clamp01(definition.LoopEnd01);
        float releaseTarget = Mathf.Clamp01(definition.ReleasePosition01);

        float crossfadeMaxSeconds = DrawCrossfadeSlider(definition, loopStart, loopEnd);
        DrawSustainGainSlider(definition);

        Rect outer = GUILayoutUtility.GetRect(10f, 116f, GUILayout.ExpandWidth(true));
        DrawPanel(outer);
        Rect strip = new Rect(outer.x + 14f, outer.y + 16f, outer.width - 28f, 42f);
        EditorGUI.DrawRect(strip, new Color(0.16f, 0.16f, 0.16f, 1f));

        if (_analysis != null && _analysis.Success && _analysis.PeakRms > 0f)
        {
            DrawEnergy(strip, _analysis);
        }
        else
        {
            GUI.Label(new Rect(strip.x + 6f, strip.y, strip.width - 12f, strip.height),
                _analysis != null && !string.IsNullOrEmpty(_analysis.Error)
                    ? _analysis.Error
                    : "点击“分析素材能量”查看波形和有声区间",
                EditorStyles.miniLabel);
        }

        DrawRegion(strip, 0f, start, HeadColor);
        DrawRegion(strip, start, Mathf.Max(0f, loopEnd - start), FirstPassColor);
        DrawRegion(strip, loopStart, Mathf.Max(0f, loopEnd - loopStart), LoopColor);
        // Release 尾巴：松手后实际会发声的那一段。
        // 不开 SeekToReleaseRegion 时尾巴 = LoopEnd 到素材结尾；开了则从 ReleasePosition01 开始。
        float tailStart = Mathf.Clamp01(definition.SeekToReleaseRegion ? releaseTarget : loopEnd);
        DrawRegion(strip, tailStart, Mathf.Max(0f, 1f - tailStart),
            definition.SeekToReleaseRegion
                ? ReleaseTargetColor
                : new Color(ReleaseTargetColor.r, ReleaseTargetColor.g, ReleaseTargetColor.b, 0.45f));
        DrawLoopWrap(strip, loopStart, loopEnd);
        // 绿色尾巴区间本身已经标出了起点，不再额外画一条同色的 Release 目标线，
        // 否则开启 SeekToReleaseRegion 时会出现“绿块 + 绿线”重叠。

        HandleRegionDrag(definition, strip);
        string hover = BuildRegionHoverHint(
            definition, strip, start, loopStart, loopEnd, releaseTarget, tailStart);
        DrawLegend(new Rect(outer.x + 14f, outer.y + 62f, outer.width - 28f, 16f));

        GUI.Label(new Rect(outer.x + 14f, outer.y + 82f, outer.width - 28f, 18f),
            $"{(definition.LoopsWhileHeld ? "按住循环" : "不循环")} · Loop {loopStart:0.00}..{loopEnd:0.00}" +
            (definition.SeekToReleaseRegion ? $" · 松手跳到 {releaseTarget:0.00}" : " · 松手从当前位置往下播"),
            EditorStyles.miniLabel);
        GUI.Label(new Rect(outer.x + 14f, outer.y + 98f, outer.width - 28f, 18f),
            string.IsNullOrEmpty(hover)
                ? "白=起点，紫=Loop 两端，绿=Release 目标，都可以拖。鼠标悬停可看各段含义。"
                : hover,
            EditorStyles.miniLabel);

        DrawRegionWarnings(definition, loopStart, loopEnd, releaseTarget);
    }

    /// <summary>交叠时间选择器，返回当前循环区允许的上限（秒）。</summary>
    private float DrawCrossfadeSlider(
        AudioActionDefinition definition, float loopStart, float loopEnd)
    {
        float clipLength = definition.Clip != null ? definition.Clip.length : 0f;
        float loopSeconds = Mathf.Max(0f, (loopEnd - loopStart) * clipLength);
        // 上限取“250 ms”和“循环区一半”里更小的那个：交叠超过一半会把整个循环吃掉。
        float maxSeconds = definition.Clip != null
            ? Mathf.Min(0.25f, loopSeconds * 0.5f)
            : 0.25f;
        maxSeconds = Mathf.Max(0.001f, maxSeconds);

        float current = Mathf.Clamp(definition.LoopSeamFadeSeconds, 0f, maxSeconds);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("交叠时间", GUILayout.Width(56f));
        float updated = EditorGUILayout.Slider(current, 0f, maxSeconds);
        float effective = Mathf.Min(updated, maxSeconds);
        EditorGUILayout.LabelField(
            $"生效 {effective * 1000f:0} ms · 上限 {maxSeconds * 1000f:0} ms",
            EditorStyles.miniLabel, GUILayout.Width(170f));
        EditorGUILayout.EndHorizontal();

        if (!Mathf.Approximately(updated, definition.LoopSeamFadeSeconds))
        {
            Undo.RecordObject(definition, "调整交叠时间");
            definition.LoopSeamFadeSeconds = updated;
            EditorUtility.SetDirty(definition);
            GUI.changed = true;
        }
        return maxSeconds;
    }

    /// <summary>保持段循环期间的音量减益，用来把 sustain 的响度和启动段/尾音对齐。</summary>
    private static void DrawSustainGainSlider(AudioActionDefinition definition)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Loop段音量", GUILayout.Width(64f));
        float value = EditorGUILayout.Slider(
            Mathf.Clamp(definition.SustainGainDb, -24f, 6f), -24f, 6f);
        EditorGUILayout.LabelField($"{value:0.0} dB", EditorStyles.miniLabel, GUILayout.Width(64f));
        EditorGUILayout.EndHorizontal();
        if (!Mathf.Approximately(value, definition.SustainGainDb))
        {
            Undo.RecordObject(definition, "调整保持段音量");
            definition.SustainGainDb = value;
            EditorUtility.SetDirty(definition);
            GUI.changed = true;
        }
    }

    private static string BuildRegionHoverHint(
        AudioActionDefinition definition, Rect strip,
        float start, float loopStart, float loopEnd, float releaseTarget, float tailStart)
    {
        Event e = Event.current;
        if (e.type != EventType.Repaint || !strip.Contains(e.mousePosition)) return string.Empty;

        float clipLength = definition.Clip != null ? definition.Clip.length : 0f;
        float x = e.mousePosition.x;
        if (Mathf.Abs(x - RegionX(strip, start)) <= 8f)
            return $"StartPosition01 = {start:0.000}（播放头起点，{start * clipLength:0.00}s）";
        if (Mathf.Abs(x - RegionX(strip, loopStart)) <= 8f)
            return $"LoopStart01 = {loopStart:0.000}（启动段结束 / 循环起点，{loopStart * clipLength:0.00}s）";
        if (Mathf.Abs(x - RegionX(strip, loopEnd)) <= 8f)
            return $"LoopEnd01 = {loopEnd:0.000}（保持段循环结束 / 尾音起点，{loopEnd * clipLength:0.00}s）";
        if (definition.SeekToReleaseRegion &&
            Mathf.Abs(x - RegionX(strip, releaseTarget)) <= 8f)
            return $"ReleasePosition01 = {releaseTarget:0.000}（松手跳到这里，{releaseTarget * clipLength:0.00}s）";

        float position01 = Mathf.Clamp01(Mathf.InverseLerp(strip.x, strip.xMax, x));
        if (position01 < start)
            return "裁掉段：StartPosition01 之前不播放";
        if (position01 < loopStart)
            return $"启动段：{start:0.000}..{loopStart:0.000}"
                 + $"（{Mathf.Max(0f, loopStart - start) * clipLength:0.00}s，只播一次）";
        if (position01 < loopEnd)
            return $"保持段循环：{loopStart:0.000}..{loopEnd:0.000}"
                 + $"（交叠 {definition.LoopSeamFadeSeconds * 1000f:0} ms）";
        return $"尾音：{tailStart:0.000}..1.000"
             + $"（{Mathf.Max(0f, 1f - tailStart) * clipLength:0.00}s，"
             + "松手时播一次，长度就是 release 时长）";
    }

    private void DrawRegionWarnings(
        AudioActionDefinition definition, float loopStart, float loopEnd, float releaseTarget)
    {
        if (_analysis == null) return;
        if (!string.IsNullOrEmpty(_analysis.Error))
        {
            EditorGUILayout.HelpBox(_analysis.Error, MessageType.Info);
            return;
        }
        if (!_analysis.Success || _analysis.PeakRms <= 0f) return;

        float loopEnergy = _analysis.AverageRms01(loopStart, loopEnd);
        if (loopEnergy < _analysis.PeakRms * 0.05f)
        {
            EditorGUILayout.HelpBox(
                $"循环区能量 {loopEnergy:0.0000} 远低于峰值 {_analysis.PeakRms:0.0000}，" +
                "按住试听会听不到持续声。用“自动设置循环区”修正，或手动改 LoopStart01 / LoopEnd01。",
                MessageType.Warning);
            return;
        }

        float seam = _analysis.SeamStep01(loopStart, loopEnd);
        if (_analysis.PeakAmplitude > 0f && seam > _analysis.PeakAmplitude * 0.12f)
        {
            EditorGUILayout.HelpBox(
                $"循环接缝跳变 {seam:0.000}（峰值 {_analysis.PeakAmplitude:0.000}），" +
                "直接循环会有明显爆音或脉动感。" +
                $"当前已用 {definition.LoopSeamFadeSeconds * 1000f:0} ms 接缝淡化削弱；" +
                "要更自然就换一段本身可无缝循环的素材。",
                MessageType.Warning);
        }

        if (definition.SeekToReleaseRegion)
        {
            float releaseEnergy = _analysis.AverageRms01(releaseTarget, 1f);
            if (releaseEnergy < _analysis.PeakRms * 0.05f)
            {
                EditorGUILayout.HelpBox(
                    $"Release 目标点 {releaseTarget:0.00} 到素材结尾几乎全是静音（能量 {releaseEnergy:0.0000}），" +
                    "开启 SeekToReleaseRegion 会让松手后没有尾巴。",
                    MessageType.Warning);
            }
        }
    }

    private void RefreshAnalysis(AudioActionDefinition definition, bool force)
    {
        if (!force && ReferenceEquals(_analysisClip, definition.Clip)) return;
        _analysisClip = definition.Clip;
        if (AudioActionSourceLocator.TryResolve(
                definition,
                out string sourceRelativePath,
                out string sourcePathError))
        {
            WwiseActionPcmSource.Data source = WwiseActionPcmSource.Load(
                sourceRelativePath,
                out string error);
            if (source != null)
            {
                _analysis = AudioActionClipAnalysis.Analyze(
                    source.Samples,
                    source.Channels,
                    source.SampleRate);
                return;
            }

            _analysis = new AudioActionClipAnalysis { Error = error };
            return;
        }

        _analysis = definition.Clip != null
            ? AudioActionClipAnalysis.Analyze(definition.Clip)
            : null;
        if (_analysis != null &&
            !_analysis.Success &&
            !string.IsNullOrEmpty(sourcePathError))
        {
            _analysis.Error = sourcePathError;
        }
    }

    private void ApplySuggestedLoop(AudioActionDefinition definition)
    {
        if (_analysis == null || !_analysis.TrySuggestLoopRegion(out float start, out float end)) return;
        Undo.RecordObject(definition, "自动设置循环区");
        definition.LoopStart01 = start;
        definition.LoopEnd01 = end;
        EditorUtility.SetDirty(definition);
        GUI.changed = true;
        Repaint();
    }

    /// <summary>
    /// 用归一化互相关在素材里找接得最顺的一对循环点（对齐 GameSynth 里颗粒/循环处理的思路）。
    /// </summary>
    private void ApplyCorrelationLoop(AudioActionDefinition definition)
    {
        if (_analysis == null) return;
        if (!_analysis.TryFindSeamlessLoopRegion(
                out float start, out float end, out float score, out float seam))
        {
            _correlationResult = "互相关没找到合适的循环点（素材太短或没有明显有声段）。";
            Repaint();
            return;
        }
        Undo.RecordObject(definition, "互相关设置循环点");
        definition.LoopStart01 = start;
        definition.LoopEnd01 = end;
        EditorUtility.SetDirty(definition);
        _correlationResult =
            $"互相关 {score:0.00} · 接缝跳变 {seam:0.0000} · " +
            $"循环 {start:0.000}..{end:0.000}（{(end - start) * definition.Clip.length:0.00}s）";
        GUI.changed = true;
        Repaint();
    }

    private static void DrawEnergy(Rect strip, AudioActionClipAnalysis analysis)
    {
        int columns = Mathf.Clamp(Mathf.RoundToInt(strip.width), 32, 512);
        if (columns < 2) return;
        float mid = strip.y + strip.height * 0.5f;
        float scale = strip.height * 0.5f - 2f;
        float peak = Mathf.Max(1e-6f, analysis.PeakRms);
        Color color = new Color(0.55f, 0.85f, 1f, 0.85f);
        for (int i = 0; i < columns; i++)
        {
            int index = Mathf.Clamp(
                Mathf.RoundToInt(i / (float)(columns - 1) * (analysis.WindowRms.Length - 1)),
                0, analysis.WindowRms.Length - 1);
            float value = Mathf.Clamp01(analysis.WindowRms[index] / peak);
            float height = Mathf.Max(0.5f, value * scale);
            float x = Mathf.Lerp(strip.x, strip.xMax - 1f, i / (float)(columns - 1));
            EditorGUI.DrawRect(new Rect(x, mid - height, 1f, height * 2f), color);
        }
    }

    private static float RegionX(Rect strip, float position01)
    {
        return Mathf.Lerp(strip.x, strip.xMax, Mathf.Clamp01(position01));
    }

    private void HandleRegionDrag(AudioActionDefinition definition, Rect strip)
    {
        float startX = RegionX(strip, definition.StartPosition01);
        float loopStartX = RegionX(strip, definition.LoopStart01);
        float loopEndX = RegionX(strip, definition.LoopEnd01);
        float releaseX = RegionX(strip, definition.ReleasePosition01);

        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        Event e = Event.current;
        switch (e.GetTypeForControl(controlId))
        {
            case EventType.MouseDown:
                if (e.button != 0 || !strip.Contains(e.mousePosition)) break;
                _draggingRegionHandle = NearestRegionHandle(
                    e.mousePosition.x, startX, loopStartX, loopEndX,
                    definition.SeekToReleaseRegion ? releaseX : float.NegativeInfinity);
                if (_draggingRegionHandle < 0) break;
                GUIUtility.hotControl = controlId;
                e.Use();
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl != controlId || _draggingRegionHandle < 0) break;
                float t = Mathf.Clamp01(Mathf.InverseLerp(strip.x, strip.xMax, e.mousePosition.x));
                Undo.RecordObject(definition, "拖动采样区域");
                switch (_draggingRegionHandle)
                {
                    case 0:
                        definition.StartPosition01 =
                            Mathf.Clamp(t, 0f, Mathf.Max(0f, definition.LoopStart01 - 0.005f));
                        break;
                    case 1:
                        definition.LoopStart01 = Mathf.Clamp(
                            t, definition.StartPosition01 + 0.005f, definition.LoopEnd01 - 0.005f);
                        break;
                    case 2:
                        definition.LoopEnd01 = Mathf.Clamp(
                            t, definition.LoopStart01 + 0.005f, 1f);
                        break;
                    case 3:
                        definition.ReleasePosition01 = t;
                        break;
                }
                EditorUtility.SetDirty(definition);
                GUI.changed = true;
                e.Use();
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl != controlId) break;
                GUIUtility.hotControl = 0;
                _draggingRegionHandle = -1;
                e.Use();
                break;
        }

        Handles.BeginGUI();
        DrawHandleLine(startX, strip, new Color(0.92f, 0.92f, 0.92f));
        DrawHandleLine(loopStartX, strip, LoopColor);
        DrawHandleLine(loopEndX, strip, LoopColor);
        // Release 手柄只在开启 SeekToReleaseRegion 时画：那时它就是绿色尾巴区间的左边界。
        // 关掉时该字段不参与发声，画出来只会和绿色区间混淆。
        if (definition.SeekToReleaseRegion)
        {
            DrawHandleLine(releaseX, strip, ReleaseTargetColor);
        }
        Handles.color = Color.white;
        Handles.EndGUI();
    }

    private static int NearestRegionHandle(
        float x, float startX, float loopStartX, float loopEndX, float releaseX)
    {
        const float pick = 12f;
        int best = -1;
        float bestDistance = pick;
        float[] positions = { startX, loopStartX, loopEndX, releaseX };
        for (int i = 0; i < positions.Length; i++)
        {
            float distance = Mathf.Abs(x - positions[i]);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    private static void DrawHandleLine(float x, Rect strip, Color color)
    {
        Handles.color = color;
        Handles.DrawAAPolyLine(3f,
            new Vector3(x, strip.y, 0f), new Vector3(x, strip.yMax, 0f));
    }

    private static void DrawRegion(Rect parent, float start01, float length01, Color color)
    {
        if (length01 <= 0f) return;
        float x = Mathf.Lerp(parent.x, parent.xMax, Mathf.Clamp01(start01));
        float width = Mathf.Lerp(parent.x, parent.xMax, Mathf.Clamp01(start01 + length01)) - x;
        if (width <= 0f) return;
        EditorGUI.DrawRect(new Rect(x, parent.y, width, parent.height),
            new Color(color.r, color.g, color.b, 0.20f));
        EditorGUI.DrawRect(new Rect(x, parent.y, width, 3f), color);
    }

    private static void DrawLoopWrap(Rect strip, float loopStart01, float loopEnd01)
    {
        if (loopEnd01 <= loopStart01) return;
        float x0 = Mathf.Lerp(strip.x, strip.xMax, Mathf.Clamp01(loopStart01));
        float x1 = Mathf.Lerp(strip.x, strip.xMax, Mathf.Clamp01(loopEnd01));
        float y = strip.yMax - 5f;
        Handles.BeginGUI();
        Handles.color = LoopColor;
        Handles.DrawAAPolyLine(2f, new Vector3(x0, y, 0f), new Vector3(x1, y, 0f));
        Handles.DrawAAPolyLine(2f,
            new Vector3(x0 + 5f, y + 4f, 0f), new Vector3(x0, y, 0f), new Vector3(x0 + 5f, y - 4f, 0f));
        Handles.color = Color.white;
        Handles.EndGUI();
    }

    private static void DrawLegend(Rect row)
    {
        float x = row.x;
        for (int i = 0; i < LegendLabels.Length; i++)
        {
            EditorGUI.DrawRect(new Rect(x, row.y + 3f, 10f, 10f), LegendColors[i]);
            x += 13f;
            var content = new GUIContent(LegendLabels[i]);
            float width = EditorStyles.miniLabel.CalcSize(content).x + 6f;
            GUI.Label(new Rect(x, row.y, width, row.height), content, EditorStyles.miniLabel);
            x += width + 4f;
        }
    }

    // ---------------- 试听 ----------------

    private void DrawAudition(AudioActionDefinition definition)
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Audition", EditorStyles.boldLabel);
        _previewSpeed = EditorGUILayout.Slider("Speed Ratio", _previewSpeed, 0f, 1f);
        _previewContact = EditorGUILayout.Slider("Contact", _previewContact, 0f, 1f);

        Rect buttonRect = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
        DrawAuditionButton(buttonRect, _previewHeld);

        // 用 hotControl 跟踪按住状态：按下时接管，鼠标即使移出按钮也一定收得到 MouseUp，
        // 不会卡在“按住”里导致松手进不了 Release。
        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        Event e = Event.current;
        switch (e.GetTypeForControl(controlId))
        {
            case EventType.MouseDown:
                if (e.button != 0 || !buttonRect.Contains(e.mousePosition)) break;
                GUIUtility.hotControl = controlId;
                StartAudition(definition);
                e.Use();
                break;
            case EventType.MouseUp:
                // 只要还在按住就结束：即使 hotControl 丢了，也不会卡在 Sustain。
                if (GUIUtility.hotControl != controlId && !_previewHeld) break;
                GUIUtility.hotControl = 0;
                EndAudition();
                e.Use();
                break;
            case EventType.MouseLeaveWindow:
                EndAudition();
                break;
        }

        // 兜底：hotControl 被别处抢走或丢失时，不要一直卡在按住状态。
        if (_previewHeld && GUIUtility.hotControl != controlId) EndAudition();

        if (_previewHeld)
        {
            double elapsed = EditorApplication.timeSinceStartup - _previewStartTime;
            float gain = WwiseActionPreviewPlayer.EvaluateGain(
                definition,
                (float)elapsed);
            _previewStatus =
                $"{WwiseActionPreviewPlayer.DescribeStage(definition, (float)elapsed)} · " +
                $"held {elapsed * 1000.0:0} ms · Gain {gain:0.00}";
        }
        else if (WwiseActionPreviewPlayer.IsActive)
        {
            _previewStatus = WwiseActionPreviewPlayer.Status;
        }

        EditorGUILayout.HelpBox(
            string.IsNullOrEmpty(_previewStatus) ? "Idle" : _previewStatus,
            MessageType.None);
    }

    private void StartAudition(AudioActionDefinition definition)
    {
        _previewHeld = true;
        _previewStartTime = EditorApplication.timeSinceStartup;
        WwiseActionPreviewPlayer.Stop();
        _previewStatus = WwiseActionPreviewPlayer.Begin(
            definition, _previewSpeed, _previewContact, out string error)
            ? "Attack"
            : error;
        if (!_previewUpdateBound)
        {
            EditorApplication.update += OnPreviewUpdate;
            _previewUpdateBound = true;
        }
        Repaint();
    }

    private void EndAudition()
    {
        if (!_previewHeld) return;
        _previewHeld = false;
        WwiseActionPreviewPlayer.Release();
        _previewStatus = WwiseActionPreviewPlayer.Status;
        Repaint();
    }

    private static void DrawAuditionButton(Rect rect, bool held)
    {
        EditorGUI.DrawRect(rect, held
            ? new Color(0.2f, 0.55f, 0.85f, 0.95f)
            : new Color(0.28f, 0.28f, 0.28f, 0.95f));
        GUI.Label(rect, "按住实时试听 · 松开进入 Release",
            new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            });
    }

    // ---------------- 通用绘制 ----------------

    private static void DrawGrid(Rect graph)
    {
        bool pro = EditorGUIUtility.isProSkin;
        Color background = pro ? new Color(0.14f, 0.14f, 0.14f, 1f) : new Color(0.92f, 0.92f, 0.92f, 1f);
        Color line = pro ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.10f);
        EditorGUI.DrawRect(graph, background);
        for (int i = 0; i <= 4; i++)
        {
            float y = Mathf.Lerp(graph.y, graph.yMax, i / 4f);
            EditorGUI.DrawRect(new Rect(graph.x, y, graph.width, 1f), line);
        }
        for (int i = 0; i <= 8; i++)
        {
            float x = Mathf.Lerp(graph.x, graph.xMax, i / 8f);
            EditorGUI.DrawRect(new Rect(x, graph.y, 1f, graph.height), line);
        }
    }

    private static void DrawPanel(Rect rect)
    {
        bool pro = EditorGUIUtility.isProSkin;
        Color background = pro ? new Color(0.12f, 0.12f, 0.12f, 1f) : new Color(0.97f, 0.97f, 0.97f, 1f);
        Color border = pro ? new Color(1f, 1f, 1f, 0.18f) : new Color(0f, 0f, 0f, 0.22f);
        EditorGUI.DrawRect(rect, background);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), border);
        EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), border);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), border);
        EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), border);
    }
}

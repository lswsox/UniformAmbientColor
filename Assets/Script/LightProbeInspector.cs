using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Text;
using System.Collections.Generic;
using System.Linq;

public class LightProbeInspector : EditorWindow
{
    private float[] availableHeights;
    private bool[] heightSelected;
    private string statusMessage = "";

    [MenuItem("Tools/Light Probe Exporter")]
    static void OpenWindow()
    {
        LightProbeInspector window = GetWindow<LightProbeInspector>("Light Probe Exporter");
        window.Initialize();
        window.Show();
    }

    void Initialize()
    {
        LightProbes probes = LightmapSettings.lightProbes;

        if (probes == null || probes.positions == null || probes.positions.Length == 0)
        {
            statusMessage = "에러: Baked Light Probe 데이터가 없습니다. Bake를 먼저 실행하세요.";
            return;
        }

        // 씬 내 고유 높이값 추출 (소수점 3자리 반올림 후 중복 제거)
        availableHeights = probes.positions
            .Select(p => Mathf.Round(p.y * 1000f) / 1000f)
            .Distinct()
            .OrderBy(h => h)
            .ToArray();

        heightSelected = new bool[availableHeights.Length];
        statusMessage = $"총 {probes.positions.Length}개 프로브, {availableHeights.Length}개 높이 감지됨.";
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Light Probe Exporter", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        if (availableHeights == null || availableHeights.Length == 0)
        {
            EditorGUILayout.HelpBox(statusMessage, MessageType.Error);
            if (GUILayout.Button("다시 시도")) Initialize();
            return;
        }

        EditorGUILayout.LabelField("내보낼 높이(PosY) 선택:", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        for (int i = 0; i < availableHeights.Length; i++)
        {
            heightSelected[i] = EditorGUILayout.ToggleLeft(
                $"Y = {availableHeights[i]:F3}", heightSelected[i]);
        }

        EditorGUILayout.Space();

        // 전체 선택/해제 버튼
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("전체 선택"))
            for (int i = 0; i < heightSelected.Length; i++) heightSelected[i] = true;
        if (GUILayout.Button("전체 해제"))
            for (int i = 0; i < heightSelected.Length; i++) heightSelected[i] = false;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        if (GUILayout.Button("CSV 내보내기"))
            Export();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
    }

    void Export()
    {
        // 선택된 높이 목록
        List<float> selectedHeights = new List<float>();
        for (int i = 0; i < availableHeights.Length; i++)
            if (heightSelected[i]) selectedHeights.Add(availableHeights[i]);

        if (selectedHeights.Count == 0)
        {
            statusMessage = "에러: 높이를 하나 이상 선택하세요.";
            return;
        }

        LightProbes probes = LightmapSettings.lightProbes;
        Vector3[] positions = probes.positions;
        SphericalHarmonicsL2[] shArray = probes.bakedProbes;

        // Point Light 정보 읽기
        Light pointLight = Object.FindFirstObjectByType<Light>();
        float lightIntensity = 0f;
        float lightIndirectMultiplier = 0f;
        float lightRange = 0f;

        if (pointLight != null && pointLight.type == LightType.Point)
        {
            lightIntensity = pointLight.intensity;
            lightIndirectMultiplier = pointLight.bounceIntensity;
            lightRange = pointLight.range;
        }
        else
        {
            statusMessage = "경고: 씬에서 Point Light를 찾지 못했습니다.";
        }

        // 선택된 높이에 해당하는 프로브 필터링
        List<(float x, float z, float luminance)> filteredData = new List<(float, float, float)>();

        for (int i = 0; i < positions.Length; i++)
        {
            float roundedY = Mathf.Round(positions[i].y * 1000f) / 1000f;
            if (!selectedHeights.Contains(roundedY)) continue;

            SphericalHarmonicsL2 sh = shArray[i];
            float r = sh[0, 0];
            float g = sh[1, 0];
            float b = sh[2, 0];
            float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;

            filteredData.Add((positions[i].x, positions[i].z, luminance));
        }

        if (filteredData.Count == 0)
        {
            statusMessage = "에러: 선택된 높이에 해당하는 프로브가 없습니다.";
            return;
        }

        // --- CSV 1: 프로브 데이터 ---
        StringBuilder sbProbe = new StringBuilder();
        sbProbe.AppendLine("PosX,PosZ,Luminance");
        foreach (var d in filteredData)
            sbProbe.AppendLine($"{d.x:F3},{d.z:F3},{d.luminance:F4}");

        string heightTag = string.Join("_", selectedHeights.Select(h => $"Y{h:F3}"));
        string probePath = Application.dataPath + $"/LightProbeData_{heightTag}.csv";
        var utf8Bom = new System.Text.UTF8Encoding(true);
        System.IO.File.WriteAllText(probePath, sbProbe.ToString(), utf8Bom);

        // --- CSV 2: 균등도 지표 ---
        var luminances = filteredData.Select(d => d.luminance).ToList();
        float min = luminances.Min();
        float max = luminances.Max();
        float mean = luminances.Average();
        float variance = luminances.Select(l => (l - mean) * (l - mean)).Average();
        float stdDev = Mathf.Sqrt(variance);
        float cv = mean > 0 ? stdDev / mean : 0f;           // 변동계수 (낮을수록 균등)
        float uniformityRatio = max > 0 ? min / max : 0f;   // Min/Max Ratio (1에 가까울수록 균등)

        StringBuilder sbStats = new StringBuilder();
        sbStats.AppendLine("Metric,Value");
        sbStats.AppendLine($"ProbeCount,{filteredData.Count}");
        sbStats.AppendLine($"SelectedHeights,\"{heightTag}\"");
        sbStats.AppendLine($"Light Intensity,{lightIntensity:F4}");
        sbStats.AppendLine($"Indirect Multiplier,{lightIndirectMultiplier:F4}");
        sbStats.AppendLine($"Light Range,{lightRange:F4}");
        sbStats.AppendLine($"Min,{min:F4}");
        sbStats.AppendLine($"Max,{max:F4}");
        sbStats.AppendLine($"Mean,{mean:F4}");
        sbStats.AppendLine($"StdDev,{stdDev:F4}");
        sbStats.AppendLine($"CV (변동계수),{cv:F4}");
        sbStats.AppendLine($"UniformityRatio (Min/Max),{uniformityRatio:F4}");

        string statsPath = Application.dataPath + $"/LightProbeUniformity_{heightTag}.csv";
        System.IO.File.WriteAllText(statsPath, sbStats.ToString(), utf8Bom);

        AssetDatabase.Refresh();
        statusMessage = $"완료: {filteredData.Count}개 프로브 내보냄.\n" +
                        $"데이터: LightProbeData_{heightTag}.csv\n" +
                        $"균등도: LightProbeUniformity_{heightTag}.csv";
    }
}
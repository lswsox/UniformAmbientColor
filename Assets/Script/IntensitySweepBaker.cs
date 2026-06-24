using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Text;
using System.Collections.Generic;
using System.Linq;

public class IntensitySweepBaker : EditorWindow
{
    // --- UI 입력값 ---
    private int testCount = 5;
    private float intensityStart = 0.1f;
    private float intensityEnd = 1.0f;
    private float indirectStart = 1.0f;
    private float indirectEnd = 10.0f;

    // 높이 선택
    private float[] availableHeights;
    private bool[] heightSelected;

    // 실행 상태
    private bool isBaking = false;
    private int currentStep = 0;
    private List<(float intensity, float indirect, float cv, float uniformityRatio)> results;
    private float targetHeight = 0f;
    private string statusMessage = "파라미터를 입력하고 시작하세요.";

    [MenuItem("Tools/Intensity Sweep Baker")]
    static void OpenWindow()
    {
        IntensitySweepBaker window = GetWindow<IntensitySweepBaker>("Intensity Sweep Baker");
        window.RefreshHeights();
        window.Show();
    }

    void RefreshHeights()
    {
        LightProbes probes = LightmapSettings.lightProbes;
        if (probes == null || probes.positions == null || probes.positions.Length == 0)
        {
            LightProbeGroup[] groups = Object.FindObjectsByType<LightProbeGroup>(FindObjectsSortMode.None);
            if (groups.Length == 0)
            {
                statusMessage = "씬에 Light Probe Group이 없습니다.";
                return;
            }

            var heights = groups
                .SelectMany(g => g.probePositions.Select(p => g.transform.TransformPoint(p).y))
                .Select(y => Mathf.Round(y * 1000f) / 1000f)
                .Distinct()
                .OrderBy(h => h)
                .ToArray();

            availableHeights = heights;
        }
        else
        {
            availableHeights = probes.positions
                .Select(p => Mathf.Round(p.y * 1000f) / 1000f)
                .Distinct()
                .OrderBy(h => h)
                .ToArray();
        }

        heightSelected = new bool[availableHeights.Length];
        statusMessage = $"{availableHeights.Length}개 높이 감지됨. 하나만 선택하세요.";
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Intensity & Indirect Multiplier Sweep Baker", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUI.BeginDisabledGroup(isBaking);

        EditorGUILayout.LabelField("테스트 설정", EditorStyles.boldLabel);
        testCount = EditorGUILayout.IntField("테스트 횟수", testCount);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Intensity 범위", EditorStyles.boldLabel);
        intensityStart = EditorGUILayout.FloatField("Intensity Start", intensityStart);
        intensityEnd = EditorGUILayout.FloatField("Intensity End", intensityEnd);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Indirect Multiplier 범위", EditorStyles.boldLabel);
        indirectStart = EditorGUILayout.FloatField("Indirect Multiplier Start", indirectStart);
        indirectEnd = EditorGUILayout.FloatField("Indirect Multiplier End", indirectEnd);

        EditorGUILayout.Space();

        // 현재 스텝 미리보기
        if (testCount > 0)
        {
            EditorGUILayout.LabelField("스텝 미리보기", EditorStyles.boldLabel);
            int previewCount = Mathf.Min(testCount, 5);
            for (int i = 0; i < previewCount; i++)
            {
                float t = testCount > 1 ? (float)i / (testCount - 1) : 0f;
                float previewIntensity = Mathf.Lerp(intensityStart, intensityEnd, t);
                float previewIndirect = Mathf.Lerp(indirectStart, indirectEnd, t);
                EditorGUILayout.LabelField($"  Step {i + 1}: Intensity={previewIntensity:F4}, Indirect={previewIndirect:F4}");
            }
            if (testCount > 5)
                EditorGUILayout.LabelField($"  ... (총 {testCount}개)");
        }

        EditorGUILayout.Space();

        // 높이 선택
        EditorGUILayout.LabelField("측정 높이(PosY) 선택 (하나만):", EditorStyles.boldLabel);
        if (availableHeights != null)
        {
            for (int i = 0; i < availableHeights.Length; i++)
            {
                bool newVal = EditorGUILayout.ToggleLeft(
                    $"Y = {availableHeights[i]:F3}", heightSelected[i]);

                if (newVal && !heightSelected[i])
                {
                    for (int j = 0; j < heightSelected.Length; j++)
                        heightSelected[j] = false;
                    heightSelected[i] = true;
                }
                else
                {
                    heightSelected[i] = newVal;
                }
            }
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("높이 목록 새로고침"))
            RefreshHeights();

        EditorGUILayout.Space();

        if (GUILayout.Button("스윕 시작"))
            StartSweep();

        EditorGUI.EndDisabledGroup();

        if (isBaking && GUILayout.Button("중단"))
            CancelSweep();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
    }

    void StartSweep()
    {
        if (heightSelected == null || !heightSelected.Any(h => h))
        {
            statusMessage = "에러: 높이를 하나 선택하세요.";
            return;
        }
        if (testCount <= 0)
        {
            statusMessage = "에러: 테스트 횟수는 1 이상이어야 합니다.";
            return;
        }
        if (intensityStart < 0 || indirectStart < 0)
        {
            statusMessage = "에러: Intensity와 Indirect Multiplier는 0 이상이어야 합니다.";
            return;
        }

        Light pointLight = Object.FindFirstObjectByType<Light>();
        if (pointLight == null || pointLight.type != LightType.Point)
        {
            statusMessage = "에러: 씬에 Point Light가 없습니다.";
            return;
        }

        targetHeight = availableHeights[System.Array.IndexOf(heightSelected, true)];
        results = new List<(float, float, float, float)>();
        currentStep = 0;
        isBaking = true;

        statusMessage = $"스윕 시작... (0 / {testCount})";
        BakeNextStep();
    }

    void BakeNextStep()
    {
        if (currentStep >= testCount)
        {
            FinishSweep();
            return;
        }

        float t = testCount > 1 ? (float)currentStep / (testCount - 1) : 0f;
        float currentIntensity = Mathf.Lerp(intensityStart, intensityEnd, t);
        float currentIndirect = Mathf.Lerp(indirectStart, indirectEnd, t);

        Light pointLight = Object.FindFirstObjectByType<Light>();
        pointLight.intensity = currentIntensity;
        pointLight.bounceIntensity = currentIndirect;

        EditorUtility.SetDirty(pointLight);

        statusMessage = $"Baking... Intensity={currentIntensity:F4}, Indirect={currentIndirect:F4} ({currentStep + 1} / {testCount})";
        Repaint();

        Lightmapping.bakeCompleted += OnBakeCompleted;
        Lightmapping.BakeAsync();
    }

    void OnBakeCompleted()
    {
        Lightmapping.bakeCompleted -= OnBakeCompleted;

        float t = testCount > 1 ? (float)currentStep / (testCount - 1) : 0f;
        float currentIntensity = Mathf.Lerp(intensityStart, intensityEnd, t);
        float currentIndirect = Mathf.Lerp(indirectStart, indirectEnd, t);

        (float cv, float uniformityRatio) = CalculateUniformity(targetHeight);
        results.Add((currentIntensity, currentIndirect, cv, uniformityRatio));

        statusMessage = $"완료: Intensity={currentIntensity:F4}, Indirect={currentIndirect:F4}, CV={cv:F4}, Ratio={uniformityRatio:F4} ({currentStep + 1}/{testCount})";
        Repaint();

        currentStep++;
        BakeNextStep();
    }

    (float cv, float uniformityRatio) CalculateUniformity(float height)
    {
        LightProbes probes = LightmapSettings.lightProbes;
        if (probes == null) return (0f, 0f);

        Vector3[] positions = probes.positions;
        SphericalHarmonicsL2[] shArray = probes.bakedProbes;

        List<float> luminances = new List<float>();

        for (int i = 0; i < positions.Length; i++)
        {
            float roundedY = Mathf.Round(positions[i].y * 1000f) / 1000f;
            if (Mathf.Abs(roundedY - height) > 0.001f) continue;

            SphericalHarmonicsL2 sh = shArray[i];
            float r = sh[0, 0];
            float g = sh[1, 0];
            float b = sh[2, 0];
            float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            luminances.Add(luminance);
        }

        if (luminances.Count == 0) return (0f, 0f);

        float min = luminances.Min();
        float max = luminances.Max();
        float mean = luminances.Average();
        float variance = luminances.Select(l => (l - mean) * (l - mean)).Average();
        float stdDev = Mathf.Sqrt(variance);
        float cv = mean > 0 ? stdDev / mean : 0f;
        float uniformityRatio = max > 0 ? min / max : 0f;

        return (cv, uniformityRatio);
    }

    void FinishSweep()
    {
        isBaking = false;

        Light pointLight = Object.FindFirstObjectByType<Light>();
        float lightRange = pointLight != null ? pointLight.range : 0f;

        StringBuilder sb = new StringBuilder();
        var utf8Bom = new System.Text.UTF8Encoding(true);

        sb.AppendLine("Light Range,Intensity,Indirect Multiplier,CV,UniformityRatio (Min/Max)");
        foreach (var r in results)
            sb.AppendLine($"{lightRange:F4},{r.intensity:F4},{r.indirect:F4},{r.cv:F4},{r.uniformityRatio:F4}");

        string path = Application.dataPath + $"/IntensitySweepResult_Y{targetHeight:F3}.csv";
        System.IO.File.WriteAllText(path, sb.ToString(), utf8Bom);
        AssetDatabase.Refresh();

        statusMessage = $"스윕 완료. 결과 저장: IntensitySweepResult_Y{targetHeight:F3}.csv";
        Repaint();
    }

    void CancelSweep()
    {
        Lightmapping.bakeCompleted -= OnBakeCompleted;
        Lightmapping.Cancel();
        isBaking = false;
        statusMessage = "스윕이 중단되었습니다.";
    }
}

using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Text;
using System.Collections.Generic;
using System.Linq;

public class RangeSweepBaker : EditorWindow
{
    // --- UI 입력값 ---
    private float rangeStart = 5f;
    private float rangeStep = 1f;
    private int testCount = 5;

    // 높이 선택
    private float[] availableHeights;
    private bool[] heightSelected;

    // 실행 상태
    private bool isBaking = false;
    private int currentStep = 0;
    private List<(float range, float cv, float uniformityRatio)> results;
    private float targetHeight = 0f;
    private string statusMessage = "파라미터를 입력하고 시작하세요.";

    [MenuItem("Tools/Range Sweep Baker")]
    static void OpenWindow()
    {
        RangeSweepBaker window = GetWindow<RangeSweepBaker>("Range Sweep Baker");
        window.RefreshHeights();
        window.Show();
    }

    void RefreshHeights()
    {
        LightProbes probes = LightmapSettings.lightProbes;
        if (probes == null || probes.positions == null || probes.positions.Length == 0)
        {
            // Bake 전이므로 프로브 위치만 씬에서 직접 읽기
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
        EditorGUILayout.LabelField("Range Sweep Baker", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // 파라미터 입력
        EditorGUI.BeginDisabledGroup(isBaking);

        EditorGUILayout.LabelField("Point Light Range 설정", EditorStyles.boldLabel);
        rangeStart = EditorGUILayout.FloatField("시작값 (Range Start)", rangeStart);
        rangeStep = EditorGUILayout.FloatField("Step 크기 (Range Step)", rangeStep);
        testCount = EditorGUILayout.IntField("테스트 횟수", testCount);

        EditorGUILayout.Space();

        // 높이 선택
        EditorGUILayout.LabelField("측정 높이(PosY) 선택 (하나만):", EditorStyles.boldLabel);
        if (availableHeights != null)
        {
            for (int i = 0; i < availableHeights.Length; i++)
            {
                bool newVal = EditorGUILayout.ToggleLeft(
                    $"Y = {availableHeights[i]:F3}", heightSelected[i]);

                // 하나만 선택되도록 강제
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
        // 유효성 검사
        if (heightSelected == null || !heightSelected.Any(h => h))
        {
            statusMessage = "에러: 높이를 하나 선택하세요.";
            return;
        }
        if (testCount <= 0 || rangeStep <= 0)
        {
            statusMessage = "에러: 테스트 횟수와 Step은 0보다 커야 합니다.";
            return;
        }

        Light pointLight = Object.FindFirstObjectByType<Light>();
        if (pointLight == null || pointLight.type != LightType.Point)
        {
            statusMessage = "에러: 씬에 Point Light가 없습니다.";
            return;
        }

        targetHeight = availableHeights[System.Array.IndexOf(heightSelected, true)];
        results = new List<(float, float, float)>();
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

        float currentRange = rangeStart + rangeStep * currentStep;

        Light pointLight = Object.FindFirstObjectByType<Light>();
        pointLight.range = currentRange;

        // 씬 변경 사항 저장
        EditorUtility.SetDirty(pointLight);

        statusMessage = $"Baking... Range={currentRange:F2} ({currentStep + 1} / {testCount})";
        Repaint();

        // Bake 완료 콜백 등록
        Lightmapping.bakeCompleted += OnBakeCompleted;
        Lightmapping.BakeAsync();
    }

    void OnBakeCompleted()
    {
        Lightmapping.bakeCompleted -= OnBakeCompleted;

        float currentRange = rangeStart + rangeStep * currentStep;

        // 균등도 계산
        (float cv, float uniformityRatio) = CalculateUniformity(targetHeight);
        results.Add((currentRange, cv, uniformityRatio));

        statusMessage = $"완료: Range={currentRange:F2}, CV={cv:F4}, Ratio={uniformityRatio:F4} ({currentStep + 1}/{testCount})";
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
        float intensity = pointLight != null ? pointLight.intensity : 0f;
        float indirectMultiplier = pointLight != null ? pointLight.bounceIntensity : 0f;

        StringBuilder sb = new StringBuilder();
        var utf8Bom = new System.Text.UTF8Encoding(true);

        sb.AppendLine("Light Intensity,Light Indirect Multiplier,Range,CV,UniformityRatio (Min/Max)");
        foreach (var r in results)
            sb.AppendLine($"{intensity:F4},{indirectMultiplier:F4},{r.range:F2},{r.cv:F4},{r.uniformityRatio:F4}");

        string path = Application.dataPath + $"/RangeSweepResult_Y{targetHeight:F3}.csv";
        System.IO.File.WriteAllText(path, sb.ToString(), utf8Bom);
        AssetDatabase.Refresh();

        statusMessage = $"스윕 완료. 결과 저장: RangeSweepResult_Y{targetHeight:F3}.csv";
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
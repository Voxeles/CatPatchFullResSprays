using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using CatPatchspace;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CatPatchFullResSprays;

internal static class MainMenuSpraysData
{
    private static Dictionary<SprayInstance, GameObject> _sprayToUiObjectDict = [];
    private static GameObject _templateUiObject = null;
    
    public static bool ShowSpraysOnMainMenu
    {
        get => Plugin.Instance.ConfigShowSpraysOnMainMenu.Value;
        set
        {
            if (value == ShowSpraysOnMainMenu)
                return;

            Plugin.Instance.ConfigShowSpraysOnMainMenu.Value = value;

            CleanupDict();

            var activeSprays = SprayNetworkController._instance?._visualController?._activeSprays;
            if (activeSprays == null)
                return;

            foreach (var spray in activeSprays)
            {
                if (spray == null)
                    continue;
                UpdateSpray(spray.GetComponent<SprayInstance>(), value);
            }
        }
    }

    public static void CleanupDict()
    {
        var activeSprays = SprayNetworkController._instance?._visualController._activeSprays;
        if (activeSprays == null)
            return;

        foreach (var pair in _sprayToUiObjectDict.ToList())
        {
            if (pair.Key != null && activeSprays.Contains(pair.Key.gameObject))
                continue;
            _sprayToUiObjectDict.Remove(pair.Key);
            Object.Destroy(pair.Value);
        }
    }

    public static void Reset()
    {
        foreach (var obj in _sprayToUiObjectDict.Values)
        {
            if (obj == null)
                continue;
            Object.Destroy(obj);
        }
        _sprayToUiObjectDict.Clear();
    }

    public static void RefreshSpray(SprayInstance spray)
    {
        if (_sprayToUiObjectDict.TryGetValue(spray, out var obj))
        {
            _sprayToUiObjectDict.Remove(spray);
            Object.Destroy(obj);
        }
        if (KrokoshaCasualtiesUtils.Util.IsInMainMenu())
            UpdateSpray(spray, ShowSpraysOnMainMenu);
    }
    
    private static void InitTemplateUiObject()
    {
        _templateUiObject = Object.Instantiate(PreRunScript.instance.transform.Find("Credits/TranslationRepo").gameObject);
        Object.Destroy(_templateUiObject.GetComponent<UITooltip>());
        Object.Destroy(_templateUiObject.GetComponent<Button>());
        var baseRect = _templateUiObject.GetComponent<RectTransform>();
        baseRect.pivot = new Vector2(0.5f, 0.5f);
        baseRect.anchorMin = new Vector2(0.5f, 0.5f);
        baseRect.anchorMax = new Vector2(0.5f, 0.5f);
        _templateUiObject.SetActive(false);
    }

    public static void UpdateSpray(SprayInstance spr, bool show)
    {
        if (!KrokoshaCasualtiesUtils.Util.IsInMainMenu() || spr == null || PreRunScript.instance == null || Camera.main == null)
            return;
        
        if (_sprayToUiObjectDict.TryGetValue(spr, out GameObject uiObj))
        {
            uiObj.SetActive(show);
            return;
        }
        
        if (_templateUiObject == null)
            InitTemplateUiObject();

        var preRun = PreRunScript.instance;
        var sprite = spr.GetComponent<SpriteRenderer>().sprite;
        var lb = sprite.bounds;
        var t = spr.transform;
        var cam = Camera.main;
        var screenBL = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(lb.min.x, lb.min.y, 0)));
        var screenBR = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(lb.max.x, lb.min.y, 0)));
        var screenTL = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(lb.min.x, lb.max.y, 0)));
        var screenCenter = cam.WorldToScreenPoint(t.TransformPoint(lb.center));
        var width  = Vector2.Distance(screenBL, screenBR);
        var height = Vector2.Distance(screenBL, screenTL);
        var rightDir = screenBR - screenBL;
        var angle = Mathf.Atan2(rightDir.y, rightDir.x) * Mathf.Rad2Deg;
        var canvas = Object.FindObjectOfType<PreRunScript>().GetComponent<Canvas>();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            (RectTransform)preRun.transform, screenCenter, null, out Vector2 localPoint);
        
        uiObj = Object.Instantiate(_templateUiObject, preRun.transform, false);
        uiObj.SetActive(show);
        var img = uiObj.GetComponent<Image>();
        img.sprite = sprite;
        var rect = uiObj.GetComponent<RectTransform>();
        rect.SetSiblingIndex(preRun.transform.Find("Logo").GetSiblingIndex());
        rect.anchoredPosition = localPoint;
        rect.sizeDelta = new Vector2(width, height) / canvas.scaleFactor / 8;
        rect.localRotation = Quaternion.Euler(0, 0, angle);
        _sprayToUiObjectDict.Add(spr, uiObj);
    }
}

[HarmonyPatch(typeof(SprayVisualController))]
internal static class CatPatchCreateSprayPatch
{
    [HarmonyPatch(nameof(SprayVisualController.TryCreateSpray))]
    [HarmonyPostfix]
    private static void TryCreateSprayPostfix(bool __result, ref GameObject sprayObject)
    {
        if (!__result)
            return;
        if (KrokoshaCasualtiesUtils.Util.IsInMainMenu())
            MainMenuSpraysData.UpdateSpray(sprayObject.GetComponent<SprayInstance>(), MainMenuSpraysData.ShowSpraysOnMainMenu);
    }
    
    [HarmonyPatch(nameof(SprayVisualController.ClearActiveSprays))]
    [HarmonyPostfix]
    private static void ClearActiveSpraysPostfix() => MainMenuSpraysData.CleanupDict();

    [HarmonyPatch(nameof(SprayVisualController.ClearActiveSpraysByOwner))]
    [HarmonyPostfix]
    private static void ClearActiveSpraysByOwnerPostfix() => MainMenuSpraysData.CleanupDict();
    
    [HarmonyPatch(nameof(SprayVisualController.ClearActiveSpray))]
    [HarmonyPostfix]
    private static void ClearActiveSprayPostfix() => MainMenuSpraysData.CleanupDict();
}

[HarmonyPatch(typeof(CatPatchHostSettingsMenu), nameof(CatPatchHostSettingsMenu.DrawWindowContents))]
internal static class CatPatchSettingsAddTogglePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .MatchForward(false,
                new CodeMatch(OpCodes.Call,
                    AccessTools.PropertyGetter(typeof(CatPatchClientSettings),
                        nameof(CatPatchClientSettings.ShowSprays))))
            .ThrowIfInvalid("CatPatchSettingsAddTogglePatch.Transpiler could not find a match!")
            .InsertAndAdvance(
                new CodeInstruction(OpCodes.Ldstr, "Show sprays on main menu"),
                new CodeInstruction(OpCodes.Call,
                    AccessTools.PropertyGetter(typeof(MainMenuSpraysData), nameof(MainMenuSpraysData.ShowSpraysOnMainMenu))),
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(CatPatchHostSettingsMenu), nameof(CatPatchHostSettingsMenu.DrawToggleRow))),
                new CodeInstruction(OpCodes.Call,
                    AccessTools.PropertySetter(typeof(MainMenuSpraysData), nameof(MainMenuSpraysData.ShowSpraysOnMainMenu)))
                )
            .InstructionEnumeration();
    }
}

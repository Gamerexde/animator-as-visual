﻿#if UNITY_EDITOR

using System.Collections.Generic;
using System.Linq;

using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

using AnimatorAsCode.Pi.V0;
using VRC.SDK3.Avatars.ScriptableObjects;
using System;

namespace pi.AnimatorAsVisual
{
    public partial class AavGenerator
    {
        public const string GeneratedFolder = "Generated-AAV";

        public AacFlBase AAC { get; private set; }
        public AnimatorAsVisual AAV { get; private set; }
        public AacFlLayer MainFX { get; private set; }
        
        private readonly List<string> usedParams = new List<string>();
        private readonly List<(Motion on, Motion off, AacFlFloatParameter param)> blendTreeMotions = new List<(Motion on, Motion off, AacFlFloatParameter param)>();

        public int StatsBlendTreeMotions => blendTreeMotions.Count;
        public int StatsUsedParameters => usedParams.Count;
        public int StatsUpdatedUsedParameters { get; private set; }
        public int StatsLayers { get; private set; }

        public string LastStatsSummary { get; private set; }
        public string RemotingString { get; private set; }

        private static readonly HashSet<IAavGeneratorHook> hooks = new HashSet<IAavGeneratorHook>();
        public static IEnumerable<IAavGeneratorHook> Hooks => hooks;
        public static void RegisterHook(IAavGeneratorHook hook) => hooks.Add(hook);

        public AavGenerator(AnimatorAsVisual aav)
        {
            this.AAV = aav;
        }

        public void Generate()
        {
            AssetDatabase.DisallowAutoRefresh();
            AssetDatabase.StartAssetEditing();
            try
            {
                GenerateInternal();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw e; // re-throw
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.AllowAutoRefresh();
            }
        }

        private void GenerateInternal()
        {
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            var avatar = AAV.Avatar;
            usedParams.Clear();
            blendTreeMotions.Clear();
            StatsUpdatedUsedParameters = 0;
            StatsLayers = 0;

            RemotingString = null;

            var fx = (AnimatorController)avatar.baseAnimationLayers[4].animatorController;

            // generate a new asset container from scratch for performance
            EnsureGeneratedFolderExists();
            var containerPath = "Assets/" + GeneratedFolder + "/AssetContainer-" + avatar.gameObject.name + ".controller";
            AssetDatabase.DeleteAsset(containerPath);
            var container = AnimatorController.CreateAnimatorControllerAtPath(containerPath);
            container.layers = new AnimatorControllerLayer[0];

            // Generate AAC instance
            var systemName = "AAV-" + avatar.gameObject.name;
            AAC = AacV0.Create(new AacConfiguration
            {
                SystemName = systemName,
                AvatarDescriptor = avatar,
                AnimatorRoot = avatar.transform,
                DefaultValueRoot = avatar.transform,
                AssetContainer = container,
                AssetKey = "AnimatorAsVisual",
                DefaultsProvider = new AacDefaultsProvider(writeDefaults: AAV.WriteDefaults),
            });
            AAC.ClearPreviousAssets();
            
            // legacy and additional cleanup
            var allSubAssets = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(fx));
            foreach (var subAsset in allSubAssets)
            {
                if (subAsset.name.Contains($"zAutogenerated__AnimatorAsVisual_") || subAsset.name.StartsWith("AAV"))
                {
                    AssetDatabase.RemoveObjectFromAsset(subAsset);
                }
            }

            // clean previous data
            fx.layers = fx.layers.Where(l => !l.name.StartsWith("AAV-")).ToArray();
            fx.parameters = fx.parameters.Where(p => !p.name.StartsWith("AAV") && !p.name.StartsWith("RemoteAAV")).ToArray();

            AAC.RemoveAllMainLayers();
            MainFX = AAC.CreateMainFxLayer();
            MainFX.WithAvatarMaskNoTransforms(); // FIXME? make masks configurable?

            // call custom generation hooks
            foreach (var hook in hooks)
                hook.PreApply(avatar.gameObject, this);

            // generate a layer for every entry
            foreach (var item in AAV.Root.EnumerateRecursive())
                if (item.isActiveAndEnabled && item.gameObject.activeInHierarchy)
                    item.PreGenerateAnimator1(this);
            foreach (var item in AAV.Root.EnumerateRecursive())
                if (item.isActiveAndEnabled && item.gameObject.activeInHierarchy)
                    item.PreGenerateAnimator2(this);
            foreach (var item in AAV.Root.EnumerateRecursive())
            {
                AAC.RemoveAllSupportingLayers(item.ParameterName);
                if (item.isActiveAndEnabled && item.gameObject.activeInHierarchy)
                    item.GenerateAnimator(this);
            }
            foreach (var item in AAV.Root.EnumerateRecursive())
                if (item.isActiveAndEnabled && item.gameObject.activeInHierarchy)
                    item.PostGenerateAnimator1(this);
            foreach (var item in AAV.Root.EnumerateRecursive())
                if (item.isActiveAndEnabled && item.gameObject.activeInHierarchy)
                    item.PostGenerateAnimator2(this);

            // remoting
            remotingRoot = null;
            GenerateRemotingReceivers();
            GenerateRemotingSenders(MainFX);

            var data = GenerateRemotingData(AAV.Root);
            if (data.Name != null && data.Children != null && data.Children.Length > 0)
                RemotingString = JsonUtility.ToJson(data);

            // clean up Av3 parameters
            var ptmp = new List<VRCExpressionParameters.Parameter>(avatar.expressionParameters.parameters ?? new VRCExpressionParameters.Parameter[0]);
            avatar.expressionParameters.parameters = ptmp.Where(p => !p.name.StartsWith("AAV") || usedParams.Contains(p.name)).ToArray();

            if (blendTreeMotions.Count == 0)
            {
                // no need to keep main layer
                fx.layers = fx.layers.Where(l => l.name != systemName).ToArray();
            }
            else
            {
                // use main layer for combined direct blend tree motions
                var tree = AAC.NewBlendTreeAsRaw();
                tree.name = "AAVInternal-BlendTree (WD On)";
                tree.blendType = BlendTreeType.Direct;
                tree.useAutomaticThresholds = false;

                var weight = MainFX.FloatParameter("AAVInternal-BlendTree-Weight");
                MainFX.OverrideValue(weight, 1.0f);

                var childMotions = new List<ChildMotion>();

                foreach (var motion in blendTreeMotions)
                {
                    var childTree = AAC.NewBlendTreeAsRaw();
                    childTree.name = motion.param.Name;
                    childTree.blendType = BlendTreeType.Simple1D;
                    childTree.AddChild(motion.off, 0.0f);
                    childTree.AddChild(motion.on, 1.0f);
                    childTree.blendParameter = motion.param.Name;
                    childMotions.Add(new ChildMotion { motion = childTree, directBlendParameter = weight.Name, threshold = 0.0f, timeScale = 1.0f });
                }

                tree.children = childMotions.ToArray();
                tree.blendParameter = "AAVInternal-BlendTree-Weight";

                MainFX.NewState("AAVInternal-BlendTree State (WD On)").WithAnimation(tree).WithWriteDefaultsSetTo(true);
            }

            StatsLayers = fx.layers.Count(l => l.name.StartsWith("AAV") || l.name.StartsWith("RemoteAAV"));

            // call custom generation hooks
            foreach (var hook in hooks)
                hook.Apply(avatar.gameObject, this);

            // generate Av3 menu
            var menu = AAV.Menu ?? avatar.expressionsMenu;
            menu.controls.Clear();
            var allMenus = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(menu));
            foreach (var oldMenu in allMenus)
            {
                if (AssetDatabase.IsSubAsset(oldMenu))
                {
                    // destroy old sub menu data
                    ScriptableObject.DestroyImmediate(oldMenu, true);
                }
            }
            foreach (var item in AAV.Root.Items)
            {
                if (!item.isActiveAndEnabled || !item.gameObject.activeInHierarchy)
                    continue;
                var ctrl = item.GenerateAv3MenuEntry(AAV);
                if (ctrl != null)
                    menu.controls.Add(ctrl);
            }

            EditorUtility.SetDirty(menu);
            EditorUtility.SetDirty(avatar.expressionsMenu);
            EditorUtility.SetDirty(avatar.expressionParameters);

            stopwatch.Stop();

            LastStatsSummary = $"Synchronized {StatsLayers} layers + {StatsBlendTreeMotions} direct blend tree motions using {StatsUsedParameters} parameters ({StatsUpdatedUsedParameters} modified) in {stopwatch.ElapsedMilliseconds}ms";
            Debug.Log($"AAV: {LastStatsSummary}");
        }

        /*
            Helper Functions
        */
        public AacFlBoolParameter MakeAv3Parameter(AacFlLayer fx, string name, bool saved, bool @default)
        {
            var param = (AacFlBoolParameter)MakeAv3ParameterInternal(fx, name, saved, VRCExpressionParameters.ValueType.Bool, @default ? 1.0f : 0.0f);
            if (param != null)
                fx.OverrideValue(param, @default);
            return param;
        }
        public AacFlIntParameter MakeAv3Parameter(AacFlLayer fx, string name, bool saved, int @default)
        {
            var param = (AacFlIntParameter)MakeAv3ParameterInternal(fx, name, saved, VRCExpressionParameters.ValueType.Int, (float)@default);
            if (param != null)
                fx.OverrideValue(param, @default);
            return param;
        }
        public AacFlFloatParameter MakeAv3Parameter(AacFlLayer fx, string name, bool saved, float @default)
        {
            var param = (AacFlFloatParameter)MakeAv3ParameterInternal(fx, name, saved, VRCExpressionParameters.ValueType.Float, @default);
            if (param != null)
                fx.OverrideValue(param, @default);
            return param;
        }

        public AacFlFloatParameter MakeAv3ParameterBoolFloat(AacFlLayer fx, string name, bool saved, bool @default)
        {
            var param = (AacFlFloatParameter)MakeAv3ParameterInternal(fx, name, saved, VRCExpressionParameters.ValueType.Bool, @default ? 1.0f : 0.0f, forceFloatFx: true);
            if (param != null)
                fx.OverrideValue(param, @default ? 1.0f : 0.0f);
            return param;
        }

        public AacFlParameter MakeAv3ParameterInternal(AacFlLayer fx, string name, bool saved, VRCExpressionParameters.ValueType type, float @default, bool forceFloatFx = false, bool prefix = true, bool localOnly = false)
        {
            if (prefix)
                name = "AAV" + name;

            var Parameters = AAV.Avatar.expressionParameters;
            var update = false;
            var parm = Parameters.FindParameter(name);
            if (parm == null) update = true;
            else
            {
                if (parm.valueType != type || parm.defaultValue != @default || parm.saved != saved)
                {
                    var ptmp = new List<VRCExpressionParameters.Parameter>(Parameters.parameters ?? new VRCExpressionParameters.Parameter[0]);
                    ptmp.Remove(parm);
                    Parameters.parameters = ptmp.ToArray();
                    parm = null;
                    update = true;
                }
            }

            if (update)
            {
                var ptmp = new List<VRCExpressionParameters.Parameter>(Parameters.parameters ?? new VRCExpressionParameters.Parameter[0]);
                ptmp.Add(parm = new VRCExpressionParameters.Parameter()
                {
                    name = name,
                    valueType = type,
                    saved = saved,
                    defaultValue = @default,
                    networkSynced = !localOnly,
                });
                Parameters.parameters = ptmp.ToArray();
                Debug.Log("AAV: Added or updated Avatar Parameter: " + name);
                StatsUpdatedUsedParameters++;
            }

            usedParams.Add(name);

            if (forceFloatFx)
                return fx.FloatParameter(name);

            switch (type)
            {
                case VRCExpressionParameters.ValueType.Bool:
                    return fx.BoolParameter(name);
                case VRCExpressionParameters.ValueType.Int:
                    return fx.IntParameter(name);
                case VRCExpressionParameters.ValueType.Float:
                    return fx.FloatParameter(name);
                default:
                    return null;
            }
        }

        public void RegisterBlendTreeMotion(Motion on, Motion off, AacFlFloatParameter param)
        {
            blendTreeMotions.Add((on, off, param));
        }

        public static void EnsureGeneratedFolderExists()
        {
            if (!AssetDatabase.IsValidFolder("Assets/" + AavGenerator.GeneratedFolder))
            {
                AssetDatabase.CreateFolder("Assets", AavGenerator.GeneratedFolder);
            }
        }
    }
}

#endif
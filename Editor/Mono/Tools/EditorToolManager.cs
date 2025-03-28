// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using UnityEditor.Actions;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace UnityEditor.EditorTools
{
    sealed class EditorToolManager : ScriptableSingleton<EditorToolManager>
    {
        [SerializeField]
        List<ScriptableObject> m_SingletonObjects = new List<ScriptableObject>();

        [SerializeField]
        EditorTool m_ActiveTool;

        EditorActionTool m_ActiveOverride;

        Tool m_PreviousTool = Tool.Move;

        [SerializeField]
        Tool m_LastBuiltinTool = Tool.Move;

        [SerializeField]
        EditorTool m_LastCustomTool;

        [SerializeField]
        ToolVariantPrefs m_VariantPrefs = new ToolVariantPrefs();

        public ToolVariantPrefs variantPrefs => m_VariantPrefs;

        static bool s_ChangingActiveTool, s_ChangingActiveContext;

        // Mimic behavior of Tools.toolChanged for backwards compatibility until existing tools are converted to the new
        // apis.
        internal static event Action<EditorTool, EditorTool> activeToolChanged;

        [SerializeField]
        List<ComponentEditor> m_ComponentTools = new List<ComponentEditor>();

        [SerializeField]
        List<ComponentEditor> m_ComponentContexts = new List<ComponentEditor>();

        // unfiltered component tools includes locked inspectors
        internal IEnumerable<ComponentEditor> componentTools => m_ComponentTools;

        internal static IEnumerable<ComponentEditor> componentContexts => instance.m_ComponentContexts;

        internal static int availableComponentContextCount => instance.m_ComponentContexts.Count;

        internal static IEnumerable<Type> additionalContextToolTypesCache = Enumerable.Empty<Type>();

        [SerializeField]
        EditorToolContext m_ActiveToolContext;

        internal static EditorToolContext activeToolContext
        {
            get
            {
                if (instance.m_ActiveToolContext == null)
                {
                    instance.m_ActiveToolContext = GetSingleton<GameObjectToolContext>();
                    ToolManager.ActiveContextDidChange();
                    instance.m_ActiveToolContext.Activate();
                }
                return instance.m_ActiveToolContext;
            }

            set
            {
                if (s_ChangingActiveContext)
                {
                    // pop the changing state so that we don't lock the active tool after an exception is thrown.
                    s_ChangingActiveContext = false;
                    throw new InvalidOperationException("Setting the active context from EditorToolContext.OnActivated or EditorToolContext.OnWillBeDeactivated is not allowed.");
                }

                var ctx = value == null ? GetSingleton<GameObjectToolContext>() : value;

                if (ctx == activeToolContext)
                    return;

                s_ChangingActiveContext = true;

                // Make sure to get the active tool enum prior to setting the context, otherwise we'll be comparing
                // apples to oranges. Ie, the transform tools will be different despite being the same `Tool` enum value.
                var tool = Tools.current;
                var wasAdditionalContextTool = tool == Tool.Custom && additionalContextToolTypesCache.Contains(activeTool.GetType());
                var prev = instance.m_ActiveToolContext;

                if (prev != null)
                {
                    prev.Deactivate();
                }

                ToolManager.ActiveContextWillChange();
                instance.m_ActiveToolContext = ctx;

                ctx.Activate();

                instance.RebuildAvailableTools();

                var active = instance.m_ActiveTool;

                // If the previous tool was a Move, Rotate, Scale, Rect, or Transform tool we need to resolve the tool
                // type using the new context. Additionally, if the previous tool was null we'll take the opportunity
                // to assign a valid tool.
                if (EditorToolUtility.IsManipulationTool(tool) || active == null || active is NoneTool)
                {
                    var resolved = EditorToolUtility.GetEditorToolWithEnum(tool, ctx);

                    // Always try to resolve to a valid tool when switching contexts, even if it means changing the
                    // active tool type
                    for (int i = (int)Tool.Move; (resolved == null || resolved is NoneTool) && i < (int)Tool.Custom; i++)
                        resolved = EditorToolUtility.GetEditorToolWithEnum((Tool)i);

                    // If resolved is still null at this point, the setter for activeTool will substitute an instance of
                    // NoneTool for us.
                    activeTool = resolved;
                }
                // If the previous tool was an additional tool from the context, return to the Previous Persistent Tool
                // when moving to that new context
                else if(wasAdditionalContextTool)
                {
                    var isAdditionalContextTool = instance.m_ActiveToolContext.GetAdditionalToolTypes().Contains(activeTool.GetType());

                    if(!isAdditionalContextTool)
                        RestorePreviousPersistentTool();
                }

                ToolManager.ActiveContextDidChange();

                s_ChangingActiveContext = false;
            }
        }

        internal static EditorTool activeTool
        {
            get { return instance.m_ActiveTool; }

            set
            {
                if (s_ChangingActiveTool)
                {
                    // pop the changing state so that we don't lock the active tool after an exception is thrown.
                    s_ChangingActiveTool = false;
                    throw new InvalidOperationException("Attempting to set the active tool from EditorTool.OnActivate or EditorTool.OnDeactivate. This is not allowed.");
                }

                var tool = value;

                if (tool == null)
                    tool = GetSingleton<NoneTool>();

                if (tool == instance.m_ActiveTool)
                    return;

                s_ChangingActiveTool = true;

                activeOverride = null;

                ToolManager.ActiveToolWillChange();

                var previous = instance.m_ActiveTool;
                var meta = EditorToolUtility.GetMetaData(tool.GetType());

                if (previous != null)
                {
                    previous.Deactivate();

                    var previousMeta = EditorToolUtility.GetMetaData(previous.GetType());
                    var previousEnum = EditorToolUtility.GetEnumWithEditorTool(previous, activeToolContext);
                    if (previousEnum != Tool.View
                        && previousEnum != Tool.None
                        && (EditorToolUtility.IsBuiltinOverride(previous) || !EditorToolUtility.IsComponentTool(previous.GetType()))
                        // if the previous and current tools are from the same variant group, don't save the previous variant as previous tool
                        && (meta.variantGroup == null || previousMeta.variantGroup != meta.variantGroup))
                    {
                        instance.m_PreviousTool = previousEnum;

                        if (EditorToolUtility.IsManipulationTool(previousEnum))
                            instance.m_LastBuiltinTool = previousEnum;
                        else
                            instance.m_LastCustomTool = previous;
                    }
                }

                instance.m_ActiveTool = tool;
                instance.m_ActiveTool.Activate();

                ToolManager.ActiveToolDidChange();

                if (activeToolChanged != null)
                    activeToolChanged(previous, instance.m_ActiveTool);

                Tools.SyncToolEnum();
                Tools.InvalidateHandlePosition();

                if(meta.variantGroup != null)
                    instance.variantPrefs.SetPreferredVariant(meta.variantGroup, meta.editor);

                s_ChangingActiveTool = false;
            }
        }

        // this tool will transparently override the `OnToolGUI` method of the active tool.
        // do not expose this as public API with also considering how to handle lifecycle and active tool interop.
        // currently this is only used for EditorToolAction.
        internal static EditorActionTool activeOverride
        {
            get => instance.m_ActiveOverride;

            set
            {
                instance.m_ActiveOverride?.Dispose();
                instance.m_ActiveOverride = value;
            }
        }

        [Serializable]
        struct ComponentToolCache : ISerializationCallbackReceiver
        {
            [SerializeField]
            string m_ToolType;
            [SerializeField]
            string m_ContextType;

            public Type contextType;
            public Type toolType;
            public UnityObject targetObject;
            public UnityObject[] targetObjects;

            public static readonly ComponentToolCache Empty = new ComponentToolCache(null, null);

            public ComponentToolCache(EditorToolContext context, EditorTool tool)
            {
                bool customTool = IsCustomEditorTool(tool);
                bool customContext = IsCustomToolContext(context);

                if (customTool || customContext)
                {
                    toolType = customTool ? tool.GetType() : null;
                    contextType = customContext ? context.GetType() : null;
                    targetObject = tool.target;
                    targetObjects = tool.targets.ToArray();
                }
                else
                {
                    toolType = null;
                    contextType = null;
                    targetObject = null;
                    targetObjects = null;
                }

                m_ToolType = null;
                m_ContextType = null;
            }

            public bool IsEqual(ComponentEditor other)
            {
                var editor = other?.GetEditor<EditorTool>();

                if (editor == null || targetObjects == null || editor.targets == null)
                    return false;

                // todo need to cache ComponentEditor targets
                return toolType == editor.GetType() && targetObjects.SequenceEqual(editor.targets);
            }

            public override string ToString()
            {
                return $"Tool: {toolType} Context: {contextType}";
            }

            public void OnBeforeSerialize()
            {
                m_ToolType = toolType != null ? toolType.AssemblyQualifiedName : null;
                m_ContextType = contextType != null ? contextType.AssemblyQualifiedName : null;
            }

            public void OnAfterDeserialize()
            {
                if (!string.IsNullOrEmpty(m_ToolType))
                    toolType = Type.GetType(m_ToolType);
                if (!string.IsNullOrEmpty(m_ContextType))
                    contextType = Type.GetType(m_ContextType);
            }
        }

        [SerializeField]
        ComponentToolCache m_PreviousComponentToolCache;

        internal static event Action availableToolsChanged;

        void SaveComponentTool()
        {
            m_PreviousComponentToolCache = new ComponentToolCache(m_ActiveToolContext, m_ActiveTool);
        }

        EditorToolManager() {}

        void OnEnable()
        {
            Undo.undoRedoEvent += UndoRedoPerformed;
            ActiveEditorTracker.editorTrackerRebuilt += TrackerRebuilt;
            Selection.selectedObjectWasDestroyed += SelectedObjectWasDestroyed;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;

            if(activeTool != null)
                EditorApplication.delayCall += activeTool.Activate;
            if(activeToolContext != null)
                EditorApplication.delayCall += activeToolContext.Activate;
        }

        void OnDisable()
        {
            m_ActiveOverride = null;
            Undo.undoRedoEvent -= UndoRedoPerformed;
            ActiveEditorTracker.editorTrackerRebuilt -= TrackerRebuilt;
            Selection.selectedObjectWasDestroyed -= SelectedObjectWasDestroyed;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
        }

        void BeforeAssemblyReload()
        {
            if (m_ActiveTool != null)
                m_ActiveTool.Deactivate();

            if (m_ActiveToolContext != null)
                m_ActiveToolContext.Deactivate();
        }

        // used by tests
        internal static void ForceTrackerRebuild()
        {
            instance.TrackerRebuilt();
        }

        void TrackerRebuilt()
        {
            // when entering play mode there is an intermediate tracker rebuild where nothing is selected. ignore it.
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
                return;

            RebuildAvailableContexts();
            RebuildAvailableTools();
            EnsureCurrentToolIsNotNull();
        }

        void EnsureCurrentToolIsNotNull()
        {
            if (m_ActiveTool == null)
                RestorePreviousPersistentTool();
        }

        void SelectedObjectWasDestroyed(int id)
        {
            bool componentToolActive = m_ComponentTools.Any(
                x => x?.GetEditor<EditorTool>() == m_ActiveTool)
                && m_ActiveTool.m_Targets.Any(x => x == null || x.GetInstanceID() == id);

            bool componentContextActive = m_ComponentContexts.Any(
                x => x?.GetEditor<EditorToolContext>() == m_ActiveToolContext)
                && m_ActiveToolContext.targets.Any(x => x == null || x.GetInstanceID() == id);

            if (componentToolActive || componentContextActive)
            {
                SaveComponentTool();
                RestorePreviousPersistentTool();
            }
        }

        void UndoRedoPerformed(in UndoRedoInfo info)
        {
            RestoreCustomEditorTool();
        }

        void RestoreCustomEditorTool()
        {
            var restored = m_ComponentTools.FirstOrDefault(m_PreviousComponentToolCache.IsEqual);

            if (restored != null)
            {
                // todo Use generated Context
                if (m_PreviousComponentToolCache.contextType != null)
                    activeToolContext = GetComponentContext(m_PreviousComponentToolCache.contextType);

                activeTool = restored.GetEditor<EditorTool>();
            }

            m_PreviousComponentToolCache = ComponentToolCache.Empty;
        }

        // destroy invalid custom editor tools
        void ClearCustomEditorTools()
        {
            m_ActiveOverride = null;

            foreach (var customEditorTool in m_ComponentTools)
            {
                if (customEditorTool.editor == m_ActiveTool)
                    m_ActiveTool.Deactivate();
                DestroyImmediate(customEditorTool.editor);
            }

            m_ComponentTools.Clear();
        }

        void ClearComponentContexts()
        {
            foreach (var context in m_ComponentContexts)
            {
                if (context.GetEditor<EditorToolContext>() == m_ActiveToolContext)
                    m_ActiveToolContext.Deactivate();
                DestroyImmediate(context.editor);
            }

            m_ComponentContexts.Clear();
        }

        void CleanupSingletons()
        {
            for (int i = m_SingletonObjects.Count - 1; i > -1; i--)
            {
                if (m_SingletonObjects[i] == null)
                    m_SingletonObjects.RemoveAt(i);
            }
        }

        internal static T GetSingleton<T>() where T : ScriptableObject
        {
            return (T)GetSingleton(typeof(T));
        }

        internal static ScriptableObject GetSingleton(Type type)
        {
            instance.CleanupSingletons();
            if (type == null)
                return null;
            var res = default(ScriptableObject);
            for (int i = 0; i < instance.m_SingletonObjects.Count; ++i)
            {
                if (instance.m_SingletonObjects[i].GetType() == type)
                {
                    res = instance.m_SingletonObjects[i];
                    break;
                }
            }

            if (res != null)
                return res;
            res = CreateInstance(type);
            res.hideFlags = HideFlags.DontSave;
            instance.m_SingletonObjects.Add(res);
            return res;
        }

        public static EditorTool GetActiveTool()
        {
            instance.EnsureCurrentToolIsNotNull();
            return instance.m_ActiveTool;
        }

        internal EditorTool lastManipulationTool
        {
            get
            {
                var tool = (int)instance.m_LastBuiltinTool;
                var last = EditorToolUtility.GetEditorToolWithEnum((Tool)Mathf.Clamp(tool, (int)Tool.Move, (int)Tool.Custom));

                if (last != null)
                    return last;

                // if the current context doesn't support the last built-in tool, cycle through Tool until we get a valid one
                for (int i = (int)Tool.Move; i < (int)Tool.Custom; i++)
                {
                    last = EditorToolUtility.GetEditorToolWithEnum((Tool)i);

                    if (last != null)
                    {
                        activeTool = last;
                        return last;
                    }
                }

                // if the current context doesn't support any tools (???) then fall back to the builtin Move Tool
                return GetSingleton<MoveTool>();
            }
        }

        internal static Tool previousTool => instance.m_PreviousTool;

        internal static EditorTool lastCustomTool => instance.m_LastCustomTool;

        public static void RestorePreviousPersistentTool()
        {
            activeTool = instance.lastManipulationTool;
        }

        // Used by tests - EditModeAndPlayModeTests/EditorTools/EscKeyTests
        internal static bool TryPopToolState()
        {
            if(Tools.viewToolActive)
                return false;

            if(!EditorToolUtility.IsBuiltinOverride(activeTool))
            {
                RestorePreviousPersistentTool();
                return true;
            }

            if(ToolManager.activeContextType != typeof(GameObjectToolContext))
            {
                //if is in a Manipulation or additional tool leaves the current context to return to GameObject Context
                ToolManager.SetActiveContext<GameObjectToolContext>();
                return true;
            }

            return false;
        }

        static bool IsGizmoCulledBySceneCullingMasksOrFocusedScene(UnityObject uobject)
        {
            var cmp = uobject as UnityEngine.Component;
            if (cmp == null)
                return false;

            return StageUtility.IsGizmoCulledBySceneCullingMasksOrFocusedScene(cmp.gameObject, Camera.current);
        }

        internal static void OnToolGUI(EditorWindow window)
        {
            if (!IsGizmoCulledBySceneCullingMasksOrFocusedScene(activeToolContext.target))
                activeToolContext.OnToolGUI(window);

            if (instance.m_ActiveOverride != null)
            {
                instance.m_ActiveOverride.OnGUI(window);
                return;
            }

            if (Tools.s_Hidden || instance.m_ActiveTool == null)
                return;

            var current = instance.m_ActiveTool;

            if (IsGizmoCulledBySceneCullingMasksOrFocusedScene(current.target))
                return;

            using (new EditorGUI.DisabledScope(!current.IsAvailable()))
            {
                current.OnToolGUI(window);
            }

            var evt = Event.current;
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape && TryPopToolState())
                evt.Use();
        }

        static bool IsCustomEditorTool(EditorTool tool)
        {
            return EditorToolUtility.IsComponentTool(tool != null ? tool.GetType() : null);
        }

        static bool IsCustomToolContext(EditorToolContext context)
        {
            return context != null && context.GetType() != typeof(GameObjectToolContext);
        }

        void RebuildAvailableContexts()
        {
            var activeContextType = activeToolContext.GetType();
            ClearComponentContexts();
            EditorToolUtility.InstantiateComponentContexts(m_ComponentContexts);
            var restoredContext = m_ComponentContexts.Find(x => x.editorType == activeContextType);
            if (restoredContext != null)
                activeToolContext = restoredContext.GetEditor<EditorToolContext>();
        }

        void RebuildAvailableTools()
        {
            ComponentToolCache activeComponentTool = new ComponentToolCache(m_ActiveToolContext, activeTool);
            ClearCustomEditorTools();

            EditorToolUtility.InstantiateComponentTools(activeToolContext, m_ComponentTools);

            if (activeComponentTool.toolType != null)
            {
                var restoredTool = m_ComponentTools.Find(x => x.editorType == activeComponentTool.toolType);

                if (restoredTool != null)
                {
                    activeTool = restoredTool.GetEditor<EditorTool>();
                }
                else
                {
                    m_PreviousComponentToolCache = activeComponentTool;
                    RestorePreviousPersistentTool();
                }
            }

            availableToolsChanged?.Invoke();
        }

        // Used by tests
        public static T GetComponentContext<T>(bool searchLockedInspectors = false) where T : EditorToolContext
        {
            return GetComponentContext(typeof(T), searchLockedInspectors) as T;
        }

        // Used by tests
        public static EditorToolContext GetComponentContext(Type type, bool searchLockedInspectors = false)
        {
            return GetComponentContext(x => x.editorType == type && (searchLockedInspectors || !x.lockedInspector));
        }

        // Used by tests
        internal static EditorToolContext GetComponentContext(Func<ComponentEditor, bool> predicate)
        {
            foreach (var ctx in instance.m_ComponentContexts)
            {
                if (predicate(ctx))
                    return ctx.GetEditor<EditorToolContext>();
            }

            return null;
        }

        // Used by tests
        public static void GetComponentContexts(Func<ComponentEditor, bool> predicate, List<EditorToolContext> list)
        {
            list.Clear();

            foreach (var ctx in instance.m_ComponentContexts)
            {
                if (predicate(ctx))
                    list.Add(ctx.GetEditor<EditorToolContext>());
            }
        }

        internal static int GetCustomEditorToolsCount(bool includeLockedInspectorTools)
        {
            if (includeLockedInspectorTools)
                return instance.m_ComponentTools.Count;
            return instance.m_ComponentTools.Count(x => !x.lockedInspector);
        }

        // Used by tests.
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal static T GetComponentTool<T>(bool searchLockedInspectors = false)
            where T : EditorTool
        {
            return GetComponentTool(typeof(T), searchLockedInspectors) as T;
        }

        internal static EditorTool GetComponentTool(Type type, bool searchLockedInspectors = false)
        {
            return GetComponentTool(x => x.editorType == type, searchLockedInspectors);
        }

        // Get the first component tool matching a predicate.
        internal static EditorTool GetComponentTool(Func<ComponentEditor, bool> predicate, bool searchLockedInspectors)
        {
            foreach (var customEditorTool in instance.m_ComponentTools)
            {
                if (!searchLockedInspectors && customEditorTool.lockedInspector)
                    continue;

                if (predicate(customEditorTool) && customEditorTool.editor is EditorTool tool)
                    return tool;
            }

            return null;
        }

        // Checks the currently instantiated (or available global type) tools for a matching instance.
        internal static bool GetAvailableTool(EditorTypeAssociation typeAssociation, out EditorTool tool)
        {
            tool = null;

            // unlike the ToolManager interface that throws an exception, this should only log an error so as not to
            // prevent execution.
            if (!typeof(EditorTool).IsAssignableFrom(typeAssociation.editor) || typeAssociation.editor.IsAbstract)
            {
                Debug.LogError($"Invalid tool type provided by context {activeToolContext}, \"{typeAssociation.editor}\". Type must be assignable to EditorTool, and not abstract.");
                return false;
            }

            // early exit if the tool context is not applicable
            if (typeAssociation.targetContext != null && typeAssociation.targetContext != ToolManager.activeContextType)
                return false;

            // if this is a component tool
            if (typeAssociation.targetBehaviour != null && typeAssociation.targetBehaviour != typeof(NullTargetKey))
                return (tool = GetComponentTool(typeAssociation.editor, false)) != null;

            tool = (EditorTool) GetSingleton(typeAssociation.editor);

            return true;
        }

        // Collect all instantiated EditorTools for the current selection, not including locked inspectors. This is
        // what should be used to get component tools in 99% of cases. The exception is locked Inspectors, in which
        // case you can use `GetComponentTools(x => x.inspector == editor)`.
        public static void GetComponentToolsForSharedTracker(List<EditorTool> list)
        {
            var ctx = activeToolContext.GetType();

            GetComponentTools(x =>
            {
                var target_ctx = x.typeAssociation.targetContext;
                return (target_ctx == null || target_ctx == ctx) && x.editorToolScope == ComponentEditor.EditorToolScope.ComponentTool;
            }, list, false);
        }

        // Used by tests.
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal static void GetComponentTools(List<EditorTool> list, bool searchLockedInspectors)
        {
            GetComponentTools(x => true, list, searchLockedInspectors);
        }

        internal static void GetComponentTools(Func<ComponentEditor, bool> predicate,
            List<EditorTool> list,
            bool searchLockedInspectors = false)
        {
            list.Clear();

            foreach (var customEditorTool in instance.m_ComponentTools)
            {
                if (!searchLockedInspectors && customEditorTool.lockedInspector)
                    continue;

                if (predicate(customEditorTool) && customEditorTool.editor is EditorTool tool && tool.IsAvailable())
                    list.Add(tool);
            }
        }

        internal static void InvokeOnSceneGUICustomEditorTools()
        {
            foreach (var context in instance.m_ComponentContexts)
            {
                if (IsGizmoCulledBySceneCullingMasksOrFocusedScene(context.target))
                    continue;

                // ReSharper disable once SuspiciousTypeConversion.Global
                if (context.editor is IDrawSelectedHandles handle)
                    handle.OnDrawHandles();
            }

            foreach (var tool in instance.m_ComponentTools)
            {
                if (IsGizmoCulledBySceneCullingMasksOrFocusedScene(tool.target))
                    continue;

                // ReSharper disable once SuspiciousTypeConversion.Global
                if (tool.editor is IDrawSelectedHandles handle)
                    handle.OnDrawHandles();
            }
        }

        // Collect all available tools into applicable UI categories and variant groups
        public static void GetAvailableTools(List<ToolEntry> tools, EditorToolContext context = null)
        {
            if (context == null)
                context = activeToolContext;

            // at each step, check if tool is already present as a variant, and collect available variants when appending
            // 1. collect built-in tools
            // 2. collect built-in additional tools
            // 3. collect custom global tools
            // 4. collect component tools for shared tracker
            tools.Clear();

            void AddToolEntry(Type tool, ToolEntry.Scope scope)
            {
                var meta = EditorToolUtility.GetMetaData(tool);
                var entry = new ToolEntry(meta, scope);

                if (meta.variantGroup != null)
                {
                    // Because this function collects all variants when appending to the list, we can safely assume that
                    // if a variant group exists in the tools list the tool is also already appended.
                    if (tools.Any(x => x.variantGroup == meta.variantGroup
                                    && x.componentTool == entry.componentTool))
                        return;

                    foreach (var variant in EditorToolUtility.GetEditorsForVariant(meta))
                        if (GetAvailableTool(variant, out var i))
                            entry.tools.Add(i);
                }
                else if (GetAvailableTool(meta, out var i))
                {
                    entry.tools.Add(i);
                }

                if (entry.tools.Any())
                    tools.Add(entry);
            }

            // 1. builtin (transform) tools
            for (int i = (int)Tool.View; i < (int)Tool.Custom; ++i)
            {
                var tool = context.ResolveTool((Tool)i);
                if (tool != null)
                    AddToolEntry(tool, (ToolEntry.Scope) i);
            }

            // 2. builtin (additional) tools
            foreach(var tool in context.GetAdditionalToolTypes())
                AddToolEntry(tool, ToolEntry.Scope.BuiltinAdditional);

            // 3. custom global tools
            foreach(var global in EditorToolUtility.GetCustomEditorToolsForType(null))
                if(global.targetContext == null || global.targetContext == ToolManager.activeContextType)
                    AddToolEntry(global.editor, ToolEntry.Scope.CustomGlobal);

            // 4. component tools
            foreach (var tool in instance.componentTools)
                if ((tool.typeAssociation.targetContext == null ||
                     tool.typeAssociation.targetContext == context.GetType())
                    && !tool.lockedInspector
                    && !tools.Any(entry => entry.tools.Any(x => x == tool.editor)))
                    AddToolEntry(tool.editorType, ToolEntry.Scope.Component);
        }

        internal static List<ToolEntry> OrderAvailableTools(List<ToolEntry> tools)
        {
            return tools.OrderBy(x => x.scope)
                .ThenBy(x => x.priority)
                .ThenBy(x => x.GetHashCode())
                .ToList();
        }
    }
}

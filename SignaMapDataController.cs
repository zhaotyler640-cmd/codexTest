using Battlehub.RTCommon;
using Battlehub.RTEditor;

using HslCommunication.Core.Device;
using HslCommunication.Core.Net;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SignalMapping;
using UnityEngine;
[Serializable]
public class SignaMapDataController : MonoSingletonCanDestroy<SignaMapDataController>
{
    private DataManager datamanager;
    private SignalMapHandlerRegistry _handlerRegistry;
    private bool _reimportAfterUndoRedo;
    #region SignalMapData
    public List<SignalMapItemData> SignalMapDatas = new List<SignalMapItemData>();
    public Action<PLCAddressData, SignalAddressData> PLCAdressDataDeleteHandle;
    public Action MapDeleteHandle;
    protected IRTE m_editor;
    private bool initComplete = true;
    private void Awake()
    {
        _handlerRegistry = new SignalMapHandlerRegistry();
    }

    /// <summary>
    /// 装配或复制结束时插入一条映射。会创建 SignalMapItemData、注册处理器并加入列表。
    /// </summary>
    public void InsertMapData(BehaviorIOItemData Input, BehaviorIOItemData output)
    {
        if (Input == null || output == null) return;
        // 记录撤回/重做用的旧状态
        m_editor.Undo.BeginRecord();
        List<SignalMapItemData> oldData = new List<SignalMapItemData>(SignalMapDatas);

        var item = new SignalMapItemData
        {
            InputSignalData = Input,
            OutputSignalData = output,
            itemDirection = "Right"
        };
        var key = new SignalMapHandlerRegistry.StatePair(output.plcSignalType, Input.plcSignalType);
        if (_handlerRegistry.TryGetHandler(key, out var action))
        {
            action?.Invoke(output, Input, item);
        }
        else
        {
            Debug.LogWarning("InsertMapData: 未定义的组合 " + output.plcSignalType + " -> " + Input.plcSignalType);
            m_editor.Undo.EndRecord();
            return;
        }
        AddSignalMapToDatas(item);
        MapDeleteHandle?.Invoke();

        // 新状态
        List<SignalMapItemData> newData = new List<SignalMapItemData>(SignalMapDatas);
        m_editor.Undo.CreateRecord(this, newData, oldData, RedoHandle, UndoHandle);
        m_editor.Undo.EndRecord();
    }

    /// <summary>
    /// 移除一条映射（复制撤回或删除时调用）。按 Input/Output 匹配后移除并解除监听。
    /// </summary>
    public void RemoveMapData(BehaviorIOItemData Input, BehaviorIOItemData output)
    {
        if (Input == null || output == null) return;

        // 记录撤回/重做用的旧状态
        m_editor.Undo.BeginRecord();
        List<SignalMapItemData> oldData = new List<SignalMapItemData>(SignalMapDatas);

        bool removed = false;
        for (int i = SignalMapDatas.Count - 1; i >= 0; i--)
        {
            if (MatchBehaviorIO(SignalMapDatas[i].InputSignalData, Input) && MatchBehaviorIO(SignalMapDatas[i].OutputSignalData, output))
            {
                RemoveSignalMapDataByIndex(i);
                MapDeleteHandle?.Invoke();
                removed = true;
                break;
            }
        }

        // 若未移除则不创建记录
        if (!removed)
        {
            m_editor.Undo.EndRecord();
            return;
        }

        List<SignalMapItemData> newData = new List<SignalMapItemData>(SignalMapDatas);
        m_editor.Undo.CreateRecord(this, newData, oldData, RedoHandle, UndoHandle);
        m_editor.Undo.EndRecord();
    }

    private static bool MatchBehaviorIO(BehaviorIOItemData a, BehaviorIOItemData b)
    {
        if (a == b) return true;
        if (a == null || b == null) return false;
        if (a.plcSignalType != b.plcSignalType) return false;
        if (a.plcSignalType == PLCSignalType.ExternalSignal)
            return string.Equals(a.outPutBindDataId, b.outPutBindDataId, StringComparison.Ordinal) && string.Equals(a.itemFieldName, b.itemFieldName, StringComparison.Ordinal);
        if (a.plcSignalType == PLCSignalType.Robot || a.plcSignalType == PLCSignalType.Truss)
            return a.signalAddressData != null && b.signalAddressData != null && a.signalAddressData.index == b.signalAddressData.index && string.Equals(a.signalAddressData.plcname, b.signalAddressData.plcname, StringComparison.Ordinal);
        if (a.plcSignalType == PLCSignalType.PLC)
            return a.plcData != null && b.plcData != null && string.Equals(a.plcData.plcname, b.plcData.plcname, StringComparison.Ordinal) && string.Equals(a.plcData.RealReadaddress, b.plcData.RealReadaddress, StringComparison.Ordinal);
        return false;
    }
    private void Start()
    {
        if (SignalMapDatas == null) SignalMapDatas = new List<SignalMapItemData>();
        if (!transform.TryGetComponent<GameObjectSaveTag>(out GameObjectSaveTag gs))
        {
            gameObject.AddComponent<GameObjectSaveTag>().flag = true;
        }

        datamanager = DataManager.GetInstance();
        ImportOrExportProjectManager.Instance.ImportProjectCompleteAction += InitMapAction;

        m_editor = IOC.Resolve<IRTE>();
        m_editor.ObjectsDeleted += OnObjectDeleted;
        m_editor.ObjectsDuplicated += OnObjectsDuplicated;
        m_editor.Undo.UndoCompleted += OnUndoCompleted;
        m_editor.Undo.RedoCompleted += OnRedoCompleted;

        Globals.EventManager.Regist(EventManager.EventName_UpdateIOBindData, OnOutPutBindDataIdChange);

        Globals.EventManager.Regist(EventManager.EventName_UpdateIknameID, OnSignalOutIptActIdChange);

    }

    public void ClearSignalMpaDatas()
    {
        SignalMapDatas.Clear();
    }
    private void OnDisable()
    {
        m_editor.ObjectsDeleted -= OnObjectDeleted;
        m_editor.ObjectsDuplicated -= OnObjectsDuplicated;
        if (m_editor != null && m_editor.Undo != null)
        {
            m_editor.Undo.UndoCompleted -= OnUndoCompleted;
            m_editor.Undo.RedoCompleted -= OnRedoCompleted;
        }
        ImportOrExportProjectManager.Instance.ImportProjectCompleteAction -= InitMapAction;
        Globals.EventManager.UnRegist(EventManager.EventName_UpdateIOBindData, OnOutPutBindDataIdChange);
        Globals.EventManager.UnRegist(EventManager.EventName_UpdateIknameID, OnSignalOutIptActIdChange);
    }

    private void OnUndoCompleted()
    {
        ReimportAfterUndoRedo();
    }

    private void OnRedoCompleted()
    {
        ReimportAfterUndoRedo();
    }

    private void ReimportAfterUndoRedo()
    {
        if (!gameObject.activeInHierarchy) return;
        if (_reimportAfterUndoRedo) return;
        _reimportAfterUndoRedo = true;
        StartCoroutine(ReimportAfterUndoRedoCoroutine());
    }

    private IEnumerator ReimportAfterUndoRedoCoroutine()
    {
        // 等待一帧，确保撤回/重做后的物体已经回到场景里
        yield return null;
        _reimportAfterUndoRedo = false;
        if (SignalMapDatas == null || SignalMapDatas.Count == 0) yield break;
        StartCoroutine(ImportMapAction());
        MapDeleteHandle?.Invoke();
    }

    public bool IsProjectInitComplete()
    {
        return initComplete;
    }
    private void OnOutPutBindDataIdChange(object[] args)
    {
        if (!initComplete) return;
        foreach (var map in SignalMapDatas)
        {
            if (map.OutputSignalData == null) continue;
            if (map.OutputSignalData.plcSignalType == PLCSignalType.ExternalSignal)
            {
                if (map.OutputSignalData != null && map.OutputSignalData.outPutBindData != null)
                    map.OutputSignalData.outPutBindDataId = map.OutputSignalData.outPutBindData.Id;
            }
            if (map.InputSignalData.plcSignalType == PLCSignalType.ExternalSignal)
            {
                if (map.InputSignalData != null && map.OutputSignalData.outPutBindData != null)
                    map.InputSignalData.outPutBindDataId = map.InputSignalData.outPutBindData.Id;
            }
        }
    }
    private void OnSignalOutIptActIdChange(object[] args)
    {
        if (!initComplete) return;
        foreach (var map in SignalMapDatas)
        {
            if (map.OutputSignalData == null) continue;
            if (map.OutputSignalData.plcSignalType == PLCSignalType.Robot || map.OutputSignalData.plcSignalType == PLCSignalType.Truss)
            {
                if (map.OutputSignalData.signalOutIptAct != null && map.OutputSignalData.signalOutIptAct != null)
                    map.OutputSignalData.signalOutDataId = map.OutputSignalData.signalOutIptAct.Id;
            }
            if (map.OutputSignalData.plcSignalType == PLCSignalType.PLC)
            {
                if (map.OutputSignalData.plcSignalData != null && map.OutputSignalData.signalOutIptAct != null)
                    map.OutputSignalData.plcSignalDataId = map.OutputSignalData.plcSignalData.Id;
            }

            if (map.InputSignalData.plcSignalType == PLCSignalType.Robot || map.InputSignalData.plcSignalType == PLCSignalType.Truss)
            {
                if (map.InputSignalData.signalOutIptAct != null && map.InputSignalData.signalOutIptAct != null)
                    map.InputSignalData.signalOutDataId = map.InputSignalData.signalOutIptAct.Id;
            }
            if (map.InputSignalData.plcSignalType == PLCSignalType.PLC)
            {
                if (map.InputSignalData.plcSignalData != null && map.InputSignalData.plcSignalData != null)
                    map.InputSignalData.plcSignalDataId = map.InputSignalData.plcSignalData.Id;
            }

        }
    }

    private void OnObjectDeleted(GameObject[] games)
    {
        for (int i = 0; i < games.Length; i++)
        {
            DeleteOutPut(games[i].GetComponentsInChildren<OutPutBindData>());

            DeleteSignalAct(games[i].GetComponentsInChildren<SignalOutIptAct>());
        }
    }

    /// <summary>
    /// 复制结束：为复制出的物体上涉及的映射关系，在新物体上插入对应映射并调用 InsertMapData。
    /// </summary>
    private void OnObjectsDuplicated(GameObject[] duplicated)
    {
        if (duplicated == null || duplicated.Length == 0 || !initComplete) return;
        GameObject[] sources = m_editor.Selection?.gameObjects;
        bool useSelectionAsSource = sources != null && sources.Length == duplicated.Length;
        var duplicatedSet = new HashSet<GameObject>(duplicated);
        var srcToDup = new Dictionary<GameObject, GameObject>();
        for (int i = 0; i < duplicated.Length; i++)
        {
            GameObject dupGo = duplicated[i];
            if (dupGo == null) continue;
            GameObject srcGo = null;
            if (useSelectionAsSource && i < sources.Length && sources[i] != dupGo)
                srcGo = sources[i];
            if (srcGo == null)
                srcGo = FindSourceGoForDuplicate(dupGo, duplicatedSet);
            if (srcGo != null)
                srcToDup[srcGo] = dupGo;
        }

        var toInsert = new List<(BehaviorIOItemData Input, BehaviorIOItemData Output)>();
        foreach (var map in SignalMapDatas)
        {
            BehaviorIOItemData outData = map.OutputSignalData;
            BehaviorIOItemData inData = map.InputSignalData;
            if (outData == null || inData == null) continue;

            // 获取源物体的OutPutBindData
            OutPutBindData outBind = GetOutPutBindDataFromBehaviorIO(outData);
            OutPutBindData inBind = GetOutPutBindDataFromBehaviorIO(inData);

            if (outBind == null && inBind == null) continue;

            GameObject srcOutGo = outBind?.gameObject;
            GameObject srcInGo = inBind?.gameObject;

            if (!srcToDup.TryGetValue(srcOutGo, out GameObject dupOutGo)) dupOutGo = null;
            if (!srcToDup.TryGetValue(srcInGo, out GameObject dupInGo)) dupInGo = null;

            if (dupOutGo == null && dupInGo == null) continue;

            BehaviorIOItemData newOutput = outData;
            BehaviorIOItemData newInput = inData;

            // 处理输出信号的复制
            if (dupOutGo != null && outBind != null)
            {
                // 在复制体中查找对应的OutPutBindData
                var dupOutPutBind = FindCorrespondingOutPutBindDataInHierarchy(dupOutGo, outBind);
                if (dupOutPutBind != null && dupOutPutBind.signalConnectUiItems != null)
                {
                    // 在signalConnectUiItems中找到对应的item
                    var item = dupOutPutBind.signalConnectUiItems.FirstOrDefault(x =>
                        x.itemFieldName == outData.itemFieldName &&
                        x.itemIoType == outData.itemIoType);
                    if (item != null) newOutput = item;
                }
            }

            // 处理输入信号的复制
            if (dupInGo != null && inBind != null)
            {
                // 在复制体中查找对应的OutPutBindData
                var dupInPutBind = FindCorrespondingOutPutBindDataInHierarchy(dupInGo, inBind);
                if (dupInPutBind != null && dupInPutBind.signalConnectUiItems != null)
                {
                    // 在signalConnectUiItems中找到对应的item
                    var item = dupInPutBind.signalConnectUiItems.FirstOrDefault(x =>
                        x.itemFieldName == inData.itemFieldName &&
                        x.itemIoType == inData.itemIoType);
                    if (item != null) newInput = item;
                }
            }

            if (newOutput != outData || newInput != inData)
                toInsert.Add((newInput, newOutput));
        }

        // 延迟执行，确保组件已初始化
        StartCoroutine(DelayedInsertMaps(toInsert));
    }

    /// <summary>
    /// 在复制体的层级中查找对应的OutPutBindData组件
    /// </summary>
    private OutPutBindData FindCorrespondingOutPutBindDataInHierarchy(GameObject duplicatedObj, OutPutBindData sourceOutPutBind)
    {
        if (duplicatedObj == null || sourceOutPutBind == null) return null;

        // 获取源物体上所有的OutPutBindData组件
        var sourceAllOutPutBinds = sourceOutPutBind.gameObject.GetComponentsInChildren<OutPutBindData>(true);
        int sourceIndex = -1;
        for (int i = 0; i < sourceAllOutPutBinds.Length; i++)
        {
            if (sourceAllOutPutBinds[i] == sourceOutPutBind)
            {
                sourceIndex = i;
                break;
            }
        }

        if (sourceIndex >= 0)
        {
            // 获取复制体上所有的OutPutBindData组件
            var dupAllOutPutBinds = duplicatedObj.GetComponentsInChildren<OutPutBindData>(true);
            if (sourceIndex < dupAllOutPutBinds.Length)
            {
                return dupAllOutPutBinds[sourceIndex];
            }
        }

        // 如果按索引找不到，尝试按相对路径查找
        string relativePath = GetRelativePath(sourceOutPutBind.transform, sourceOutPutBind.gameObject.transform);
        if (!string.IsNullOrEmpty(relativePath))
        {
            var targetTransform = duplicatedObj.transform.Find(relativePath);
            if (targetTransform != null)
            {
                return targetTransform.GetComponent<OutPutBindData>();
            }
        }

        // 最后尝试获取第一个OutPutBindData组件
        return duplicatedObj.GetComponentInChildren<OutPutBindData>(true);
    }

    /// <summary>
    /// 获取组件相对于根物体的相对路径
    /// </summary>
    private string GetRelativePath(Transform componentTransform, Transform rootTransform)
    {
        if (componentTransform == null || rootTransform == null) return string.Empty;

        var path = new System.Text.StringBuilder();
        Transform current = componentTransform;

        // 向上遍历直到根物体
        while (current != null && current != rootTransform)
        {
            if (path.Length > 0)
                path.Insert(0, "/");
            path.Insert(0, current.name);
            current = current.parent;
        }

        return path.ToString();
    }

    /// <summary>
    /// 从BehaviorIOItemData获取OutPutBindData
    /// </summary>
    private OutPutBindData GetOutPutBindDataFromBehaviorIO(BehaviorIOItemData ioData)
    {
        if (ioData == null) return null;

        // 如果直接有引用
        if (ioData.outPutBindData != null)
            return ioData.outPutBindData;

        // 通过ID查找
        if (!string.IsNullOrEmpty(ioData.outPutBindDataId))
        {
            var go = datamanager?.GetGameObject(ioData.outPutBindDataId);
            if (go != null)
                return go.GetComponentInChildren<OutPutBindData>(true);
        }

        return null;
    }

    /// <summary>
    /// 延迟插入映射
    /// </summary>
    private IEnumerator DelayedInsertMaps(List<(BehaviorIOItemData Input, BehaviorIOItemData Output)> toInsert)
    {
        yield return null; // 等待一帧，确保组件已初始化

        foreach (var t in toInsert)
        {
            if (t.Input != null && t.Output != null)
            {
                InsertMapData(t.Input, t.Output);
            }
        }
    }

    /// <summary>
    /// 根据复制体查找源物体（通过名称去掉 (Clone) 匹配同场景中未在复制列表里的对象）。
    /// </summary>
    private static GameObject FindSourceGoForDuplicate(GameObject duplicateGo, HashSet<GameObject> duplicatedSet)
    {
        if (duplicateGo == null || duplicatedSet == null) return null;
        string name = duplicateGo.name;
        string baseName = name.EndsWith("(Clone)") ? name.Substring(0, name.Length - 7) : name;
        GameObject[] roots = duplicateGo.scene.GetRootGameObjects();
        foreach (var root in roots)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (duplicatedSet.Contains(t.gameObject)) continue;
                if (t.gameObject.name == baseName) return t.gameObject;
            }
        }
        return null;
    }

    /// <summary>
    /// 删除output物体时
    /// </summary>
    public void DeleteOutPut(OutPutBindData[] outputs)
    {
        if (outputs == null || outputs.Length == 0) return;
        m_editor.Undo.BeginRecord();

        List<SignalMapItemData> oldData = new List<SignalMapItemData>(SignalMapDatas);

        if (outputs == null) return;
        //刷新信号窗口
        foreach (var item in outputs)
        {
            Instance.RemoveOutBindata(item);
        }

        SignalMapWindow smw = FindObjectOfType<SignalMapWindow>();
        if (smw != null)
        {
            smw.DeleteMapAction();
        }

        List<SignalMapItemData> newData = new List<SignalMapItemData>(SignalMapDatas);
        m_editor.Undo.CreateRecord(this, newData, oldData, RedoHandle, UndoHandle);
        m_editor.Undo.EndRecord();
    }
    private IEnumerator PerformUndo()
    {
        yield return null;
        while (true)
        {
            try
            {
                if (m_editor.Undo.CanUndo)
                {
                    m_editor.Undo.Undo();
                    break;
                }
            }
            catch (Exception)
            {
                break;
            }

            yield return null;
        }
    }
    private IEnumerator PerformRedo()
    {
        yield return null;
        while (true)
        {
            try
            {
                if (m_editor.Undo.CanRedo)
                {
                    m_editor.Undo.Redo();
                    break;
                }
            }
            catch (Exception)
            {
                break;
            }

            yield return null;
        }
    }
    /// <summary>
    /// 删除SinalAct的物体时
    /// </summary>
    /// <param name="signalOut"></param>
    public void DeleteSignalAct(SignalOutIptAct[] signalOut)
    {
        if (signalOut == null || signalOut.Length == 0) return;
        m_editor.Undo.BeginRecord();
        List<SignalMapItemData> oldData = new List<SignalMapItemData>(SignalMapDatas);
        //刷新信号窗口
        foreach (var item in signalOut)
        {
            Instance.RemovePLCSignalData(null, item);
        }

        SignalMapWindow smw = FindObjectOfType<SignalMapWindow>();
        if (smw != null)
        {
            smw.DeleteMapAction();
        }
        List<SignalMapItemData> newData = new List<SignalMapItemData>(SignalMapDatas);
        m_editor.Undo.CreateRecord(this, newData, oldData, RedoHandle, UndoHandle);

        m_editor.Undo.EndRecord();
    }
    private bool RedoHandle(Record state)
    {
        if (state.Target != null)
        {
            RemoveAllMapDatas();
            ((SignaMapDataController)state.Target).SignalMapDatas = (List<SignalMapItemData>)(state.NewState);
            Debug.Log($"SignalMapData Redo! 新状态映射数量: {((List<SignalMapItemData>)state.NewState)?.Count ?? 0}");

            // 使用协程确保顺序执行
            StartCoroutine(ExecuteRedo(state));
        }
        return true;
    }

    private IEnumerator ExecuteRedo(Record state)
    {
        // 等待数据导入完成
        yield return StartCoroutine(Instance.ImportMapAction());

        // 验证当前状态是否与预期一致
        int expectedCount = ((List<SignalMapItemData>)state.NewState)?.Count ?? 0;
        int actualCount = Instance.SignalMapDatas?.Count ?? 0;
        Debug.Log($"Redo完成验证: 预期{expectedCount}, 实际{actualCount}");

        // 刷新UI
        MapDeleteHandle?.Invoke();
    }

    private bool UndoHandle(Record state)
    {
        if (state.Target != null)
        {
            RemoveAllMapDatas();
            ((SignaMapDataController)state.Target).SignalMapDatas = (List<SignalMapItemData>)(state.OldState);
            Debug.Log($"SignalMapData Undo! 旧状态映射数量: {((List<SignalMapItemData>)state.OldState)?.Count ?? 0}");

            // 使用协程确保顺序执行
            StartCoroutine(ExecuteUndo(state));
        }
        return true;
    }

    private IEnumerator ExecuteUndo(Record state)
    {
        // 等待数据导入完成
        yield return StartCoroutine(Instance.ImportMapAction());

        // 验证当前状态是否与预期一致
        int expectedCount = ((List<SignalMapItemData>)state.OldState)?.Count ?? 0;
        int actualCount = Instance.SignalMapDatas?.Count ?? 0;
        Debug.Log($"Undo完成验证: 预期{expectedCount}, 实际{actualCount}");

        // 刷新UI
        MapDeleteHandle?.Invoke();
    }
    private void RemoveAllMapDatas()
    {
        for (int i = SignalMapDatas.Count - 1; i >= 0; i--)
        {
            RemoveSignalMapDataByIndex(i);
        }
    }
    private void InitMapAction()
    {
        if (SignalMapDatas == null) return;
        StartCoroutine("ImportMapAction");
    }
    private IEnumerator ImportMapAction()
    {
        initComplete = false;
        yield return new WaitForSeconds(0.3f);

        // 清除所有现有的事件绑定，确保撤回/重做时事件状态正确
        ClearAllExistingEventBindings();

        //需要把老项目的兼容
        CompatibleWithLegacyProjects();

        FromIdGetGameObject();

        SetActionHandle();

        ReoverPlcData();

        Globals.MQTTManager.active = true;

        initComplete = true;

        // 确保所有操作完成
        yield return null;

        // 通知数据已加载完成
        Debug.Log($"ImportMapAction完成，当前映射数量: {SignalMapDatas?.Count ?? 0}");
    }

    /// <summary>
    /// 清除所有现有的事件绑定
    /// </summary>
    private void ClearAllExistingEventBindings()
    {
        // 遍历所有映射，清除事件绑定
        foreach (var map in SignalMapDatas)
        {
            var InputSignalData = map.InputSignalData;
            var OutputSignalData = map.OutputSignalData;

            // 清除输出信号的事件绑定
            if (OutputSignalData != null)
            {
                switch (OutputSignalData.plcSignalType)
                {
                    case PLCSignalType.Robot:
                    case PLCSignalType.Truss:
                        if (OutputSignalData.signalAddressData != null)
                        {
                            OutputSignalData.signalAddressData.outputChanged = null;
                        }
                        break;
                    case PLCSignalType.PLC:
                        if (OutputSignalData.plcData != null)
                        {
                            OutputSignalData.plcData.PlcOutValueAction = null;
                        }
                        break;
                    case PLCSignalType.ExternalSignal:
                        if (OutputSignalData.outPutBindData != null)
                        {
                            OutputSignalData.outPutBindData.OutputSignalHandle = null;
                        }
                        break;
                }
            }

            // 清除输入信号的事件绑定
            if (InputSignalData != null)
            {
                switch (InputSignalData.plcSignalType)
                {
                    case PLCSignalType.Robot:
                    case PLCSignalType.Truss:
                        if (InputSignalData.signalAddressData != null)
                        {
                            InputSignalData.signalAddressData.inputChanged = null;
                        }
                        break;
                    case PLCSignalType.PLC:
                        if (InputSignalData.plcData != null)
                        {
                            InputSignalData.plcData.PlcIntValueAction = null;
                        }
                        break;
                    case PLCSignalType.ExternalSignal:
                        if (InputSignalData.outPutBindData != null)
                        {
                            InputSignalData.outPutBindData.InputSignalHandle = null;
                        }
                        break;
                }
            }

            // 清除映射本身的事件引用
            map.OutputChanged = null;
            map.PlcOutValueAction = null;
            map.OutputSignalHandle = null;
            map.inputChanged = null;
            map.PlcIntValueAction = null;
            map.InputSignalHandle = null;
        }
    }

    /// <summary>
    /// 兼容老项目
    /// </summary>
    private void CompatibleWithLegacyProjects()
    {
        foreach (var item in SignalMapDatas)
        {
            if (item.OutputSignalData != null) continue;
            item.itemDirection = "Right";
            RestoreLegacyIOData(item);
            MigratePLCSignalData(item);
        }
    }

    private void RestoreLegacyIOData(SignalMapItemData item)
    {
        const string INPUT_TYPE = "Input";
        var ioData = item.itemBehaviorIOItemData;

        if (ioData?.Count <= 0) return;

        var isInput = ioData[0].itemIoType == INPUT_TYPE;
        if (item.InputSignalData == null) item.InputSignalData = new BehaviorIOItemData();
        if (item.OutputSignalData == null) item.OutputSignalData = new BehaviorIOItemData();
        var targetSignal = isInput ? item.InputSignalData : item.OutputSignalData;

        targetSignal.itemName = ioData[0].itemName;
        targetSignal.itemFieldName = ioData[0].itemFieldName;
        targetSignal.itemIsEnum = ioData[0].itemIsEnum;
        targetSignal.itemIsEnumChild = ioData[0].itemIsEnumChild;
        targetSignal.EnumName = ioData[0].EnumName;
        targetSignal.itemIoType = ioData[0].itemIoType;
        targetSignal.itemDataType = ioData[0].itemDataType;
        targetSignal.itemDetermineValue = ioData[0].itemDetermineValue;
        targetSignal.outPutBindDataId = ioData[0].outPutBindDataId;
        targetSignal.bindDataType = ioData[0].bindDataType;
        targetSignal.isRobotOrTruss = ioData[0].isRobotOrTruss;
        item.itemBehaviorIOItemData[0].plcSignalType = PLCSignalType.ExternalSignal;
        targetSignal.plcSignalType = PLCSignalType.ExternalSignal;
    }

    private void MigratePLCSignalData(SignalMapItemData item)
    {
        const string INPUT_TYPE = "Input";
        var signalData = item.itemPLCSignalItemData;

        if (signalData == null) return;

        var isInput = signalData.itemIoType == INPUT_TYPE;
        if (item.InputSignalData == null) item.InputSignalData = new BehaviorIOItemData();
        if (item.OutputSignalData == null) item.OutputSignalData = new BehaviorIOItemData();
        var targetSignal = isInput ? item.InputSignalData : item.OutputSignalData;

        targetSignal.itemName = signalData.itemName;
        targetSignal.itemIoType = signalData.itemIoType;
        targetSignal.itemDataType = signalData.itemDataType;
        targetSignal.itemDetermineValue = signalData.itemDetermineValue;
        targetSignal.isRobotOrTruss = signalData.isRobotOrTruss;
        targetSignal.plcData = signalData.plcData;
        targetSignal.signalAddressData = signalData.signalAddressData;

        if (!string.IsNullOrEmpty(signalData.signalOutDataId))
        {
            targetSignal.signalOutDataId = signalData.signalOutDataId;
            targetSignal.plcSignalType = PLCSignalType.Robot;//先默认机器人
        }

        if (!string.IsNullOrEmpty(signalData.plcSignalDataId))
        {
            targetSignal.plcSignalDataId = signalData.plcSignalDataId;
            targetSignal.plcSignalType = PLCSignalType.PLC;
        }
    }



    private void FromIdGetGameObject()
    {
        foreach (var item in SignalMapDatas)
        {
            var InputSignalData = item.InputSignalData;
            var OutputSignalData = item.OutputSignalData;
            switch (InputSignalData.plcSignalType)
            {
                case PLCSignalType.Robot:
                    datamanager.GetGameObject(InputSignalData.signalOutDataId)?.transform.TryGetComponent<SignalOutIptAct>(out InputSignalData.signalOutIptAct);

                    break;
                case PLCSignalType.Truss:
                    datamanager.GetGameObject(InputSignalData.signalOutDataId)?.transform.TryGetComponent<SignalOutIptAct>(out InputSignalData.signalOutIptAct);

                    break;
                case PLCSignalType.PLC:
                    datamanager.GetGameObject(InputSignalData.plcSignalDataId)?.transform.TryGetComponent<PLCSignalData>(out InputSignalData.plcSignalData);

                    break;
                case PLCSignalType.ExternalSignal:
                    datamanager.GetGameObject(InputSignalData.outPutBindDataId)?.transform.TryGetComponent<OutPutBindData>(out InputSignalData.outPutBindData);
                    if (InputSignalData.outPutBindData?.signalConnectUiItems == null) break;
                    foreach (var signalUI in InputSignalData.outPutBindData.signalConnectUiItems)
                    {
                        if (signalUI.itemIoType == InputSignalData.itemIoType
                               && signalUI.itemFieldName == InputSignalData.itemFieldName)
                        {
                            signalUI.itemDetermineValue = InputSignalData.itemDetermineValue;
                        }
                    }
                    break;
                default:
                    break;
            }

            switch (OutputSignalData.plcSignalType)
            {
                case PLCSignalType.Robot:
                    datamanager.GetGameObject(OutputSignalData.signalOutDataId)?.transform.TryGetComponent<SignalOutIptAct>(out OutputSignalData.signalOutIptAct);

                    break;
                case PLCSignalType.Truss:
                    datamanager.GetGameObject(OutputSignalData.signalOutDataId)?.transform.TryGetComponent<SignalOutIptAct>(out OutputSignalData.signalOutIptAct);

                    break;
                case PLCSignalType.PLC:
                    datamanager.GetGameObject(OutputSignalData.plcSignalDataId)?.transform.TryGetComponent<PLCSignalData>(out OutputSignalData.plcSignalData);

                    break;
                case PLCSignalType.ExternalSignal:
                    datamanager.GetGameObject(OutputSignalData.outPutBindDataId)?.transform.TryGetComponent<OutPutBindData>(out OutputSignalData.outPutBindData);
                    if (OutputSignalData.outPutBindData?.signalConnectUiItems == null) break;
                    foreach (var signalUI in OutputSignalData.outPutBindData.signalConnectUiItems)
                    {
                        if (signalUI.itemIoType == OutputSignalData.itemIoType
                               && signalUI.itemFieldName == OutputSignalData.itemFieldName)
                        {
                            signalUI.itemDetermineValue = OutputSignalData.itemDetermineValue;
                        }
                    }
                    break;
                default:
                    break;
            }
        }

    }
    private void SetActionHandle()
    {
        foreach (var item in SignalMapDatas)
        {
            var InputSignalData = item.InputSignalData;
            var OutputSignalData = item.OutputSignalData;
            var key = new SignalMapHandlerRegistry.StatePair(OutputSignalData.plcSignalType, InputSignalData.plcSignalType);
            if (_handlerRegistry.TryGetHandler(key, out var action))
            {
                action?.Invoke(OutputSignalData, InputSignalData, item);
            }
            else
            {
                Debug.LogWarning("未定义的组合!");
            }
        }
    }
    private void ReoverPlcData()
    {
        foreach (var item in SignalMapDatas)
        {
            var InputSignalData = item.InputSignalData;
            var OutputSignalData = item.OutputSignalData;
            switch (InputSignalData.plcSignalType)
            {
                case PLCSignalType.Robot:
                    for (int i = 0; i < InputSignalData.signalOutIptAct.SignalIntAddress.Count; i++)
                    {
                        if (InputSignalData.signalOutIptAct.SignalIntAddress[i].index == InputSignalData.signalAddressData.index)
                        {
                            InputSignalData.signalOutIptAct.SignalIntAddress[i] = InputSignalData.signalAddressData;
                        }
                    }
                    for (int i = 0; i < InputSignalData.signalOutIptAct.AlladdressData.Count; i++)
                    {
                        if (InputSignalData.signalOutIptAct.AlladdressData[i].iotype == "Input")
                        {
                            if (InputSignalData.signalOutIptAct.AlladdressData[i].index == InputSignalData.signalAddressData.index)
                            {
                                InputSignalData.signalOutIptAct.AlladdressData[i] = InputSignalData.signalAddressData;
                            }
                        }
                    }
                    break;
                case PLCSignalType.Truss:
                    for (int i = 0; i < InputSignalData.signalOutIptAct.SignalIntAddress.Count; i++)
                    {
                        if (InputSignalData.signalOutIptAct.SignalIntAddress[i].index == InputSignalData.signalAddressData.index)
                        {
                            InputSignalData.signalOutIptAct.SignalIntAddress[i] = InputSignalData.signalAddressData;
                        }
                    }
                    for (int i = 0; i < InputSignalData.signalOutIptAct.AlladdressData.Count; i++)
                    {
                        if (InputSignalData.signalOutIptAct.AlladdressData[i].iotype == "Input")
                        {
                            if (InputSignalData.signalOutIptAct.AlladdressData[i].index == InputSignalData.signalAddressData.index)
                            {
                                InputSignalData.signalOutIptAct.AlladdressData[i] = InputSignalData.signalAddressData;
                            }
                        }
                    }
                    break;
                case PLCSignalType.PLC:
                    for (int i = 0; i < InputSignalData.plcSignalData.inputAddressDataForUI.Count; i++)
                    {
                        if (InputSignalData.plcSignalData.inputAddressDataForUI[i].plcname == InputSignalData.plcData.plcname)
                        {
                            if (InputSignalData.plcData.Endianness == "" || InputSignalData.plcData.Endianness == null)
                            {
                                InputSignalData.plcData.Endianness = "Low";
                            }
                            InputSignalData.plcSignalData.inputAddressDataForUI[i] = InputSignalData.plcData;
                        }
                    }
                    for (int i = 0; i < InputSignalData.plcSignalData.AlladdressData.Count; i++)
                    {
                        if (InputSignalData.plcSignalData.AlladdressData[i].iotype == "Input")
                        {
                            if (InputSignalData.plcSignalData.AlladdressData[i].plcname == InputSignalData.plcData.plcname)
                            {
                                InputSignalData.plcSignalData.AlladdressData[i] = InputSignalData.plcData;
                            }
                        }
                    }
                    break;
                case PLCSignalType.ExternalSignal:
                    break;
                default:
                    break;
            }

            switch (OutputSignalData.plcSignalType)
            {
                case PLCSignalType.Robot:
                case PLCSignalType.Truss:
                    for (int i = 0; i < OutputSignalData.signalOutIptAct.SignalOutAddress.Count; i++)
                    {
                        if (OutputSignalData.signalOutIptAct.SignalOutAddress[i].index == OutputSignalData.signalAddressData.index)
                        {
                            Action<int, bool, List<bool>, string> lastOutPutChange = OutputSignalData.signalOutIptAct.SignalOutAddress[i].outputChanged;
                            if (lastOutPutChange != null)
                            {
                                Delegate[] currentHandlers = OutputSignalData.signalAddressData.outputChanged?.GetInvocationList() ?? new Delegate[0];

                                foreach (Delegate handler in lastOutPutChange.GetInvocationList())
                                {
                                    bool alreadyExists = false;
                                    foreach (Delegate existingHandler in currentHandlers)
                                    {
                                        if (handler.Target == existingHandler.Target &&
                                            handler.Method == existingHandler.Method)
                                        {
                                            alreadyExists = true;
                                            break;
                                        }
                                    }

                                    if (!alreadyExists)
                                    {
                                        OutputSignalData.signalAddressData.outputChanged += (Action<int, bool, List<bool>, string>)handler;
                                    }
                                }
                            }
                            OutputSignalData.signalOutIptAct.SignalOutAddress[i] = OutputSignalData.signalAddressData;
                        }
                    }
                    for (int i = 0; i < OutputSignalData.signalOutIptAct.AlladdressData.Count; i++)
                    {
                        if (OutputSignalData.signalOutIptAct.AlladdressData[i].iotype != "Input")
                        {
                            if (OutputSignalData.signalOutIptAct.AlladdressData[i].index == OutputSignalData.signalAddressData.index)
                            {
                                OutputSignalData.signalOutIptAct.AlladdressData[i] = OutputSignalData.signalAddressData;
                            }
                        }
                    }
                    break;
                case PLCSignalType.PLC:
                    for (int i = 0; i < OutputSignalData.plcSignalData.outputAddressDataForUI.Count; i++)
                    {
                        PLCAddressData plcAddrssData = OutputSignalData.plcSignalData.outputAddressDataForUI[i];
                        if (plcAddrssData.plcname == OutputSignalData.plcData.plcname && plcAddrssData.RealReadaddress == OutputSignalData.plcData.RealReadaddress)
                        {
                            Action<string, bool, List<bool>> lastOutPutChange = plcAddrssData.PlcOutValueAction;

                            if (lastOutPutChange != null)
                            {
                                Delegate[] currentHandlers = OutputSignalData.plcData.PlcOutValueAction?.GetInvocationList() ?? new Delegate[0];

                                foreach (Delegate handler in lastOutPutChange.GetInvocationList())
                                {
                                    bool alreadyExists = false;
                                    foreach (Delegate existingHandler in currentHandlers)
                                    {
                                        if (handler.Target == existingHandler.Target &&
                                            handler.Method == existingHandler.Method)
                                        {
                                            alreadyExists = true;
                                            break;
                                        }
                                    }

                                    if (!alreadyExists)
                                    {
                                        OutputSignalData.plcData.PlcOutValueAction += (Action<string, bool, List<bool>>)handler;
                                    }
                                }
                            }
                            if (OutputSignalData.plcData.Endianness == "" || OutputSignalData.plcData.Endianness == null)
                            {
                                OutputSignalData.plcData.Endianness = "Low";
                            }
                            OutputSignalData.plcSignalData.outputAddressDataForUI[i] = OutputSignalData.plcData;
                        }
                    }
                    for (int i = 0; i < OutputSignalData.plcSignalData.AlladdressData.Count; i++)
                    {
                        if (OutputSignalData.plcSignalData.AlladdressData[i].iotype != "Input")
                        {
                            if (OutputSignalData.plcSignalData.AlladdressData[i].plcname == OutputSignalData.plcData.plcname
                                && OutputSignalData.plcSignalData.AlladdressData[i].RealReadaddress == OutputSignalData.plcData.RealReadaddress)
                            {
                                OutputSignalData.plcSignalData.AlladdressData[i] = OutputSignalData.plcData;
                            }
                        }
                    }
                    break;
                case PLCSignalType.ExternalSignal:
                    break;
                default:
                    break;
            }
        }

        foreach (var item in SignalMapDatas)
        {
            var InputSignalData = item.InputSignalData;
            var OutputSignalData = item.OutputSignalData;
            switch (OutputSignalData.plcSignalType)
            {
                case PLCSignalType.Robot:
                case PLCSignalType.Truss:
                    for (int i = 0; i < OutputSignalData.signalOutIptAct.AlladdressData.Count; i++)
                    {
                        if (OutputSignalData.signalOutIptAct.AlladdressData[i].iotype != "Input")
                        {
                            if (OutputSignalData.signalOutIptAct.AlladdressData[i].index == OutputSignalData.signalAddressData.index)
                            {
                                OutputSignalData.signalAddressData = OutputSignalData.signalOutIptAct.AlladdressData[i];
                            }
                        }
                    }
                    break;
                case PLCSignalType.PLC:
                    for (int i = 0; i < OutputSignalData.plcSignalData.AlladdressData.Count; i++)
                    {
                        if (OutputSignalData.plcSignalData.AlladdressData[i].iotype != "Input")
                        {
                            if (OutputSignalData.plcSignalData.AlladdressData[i].plcname == OutputSignalData.plcData.plcname
                                && OutputSignalData.plcSignalData.AlladdressData[i].RealReadaddress == OutputSignalData.plcData.RealReadaddress)
                            {
                                OutputSignalData.plcData = OutputSignalData.plcSignalData.AlladdressData[i];
                            }
                        }
                    }
                    break;
                case PLCSignalType.ExternalSignal:
                    break;
                default:
                    break;
            }
        }
    }

    public void AddSignalMapToDatas(SignalMapItemData data)
    {
        SignalMapDatas.Add(data);
    }
    public List<SignalMapItemData> GetSignalMapDatas()
    {
        if (!initComplete) return new List<SignalMapItemData>();
        return SignalMapDatas;
    }
    public void RemoveSignalMapDataByIndex(int index)
    {
        try
        {
            //移除监听
            SignalMapItemData signalmapdata = SignalMapDatas[index];

            BehaviorIOItemData inputSignalData = signalmapdata.InputSignalData;
            BehaviorIOItemData outputSignalData = signalmapdata.OutputSignalData;



            switch (outputSignalData.plcSignalType)
            {
                case PLCSignalType.Robot:
                    if (outputSignalData.signalAddressData?.outputChanged != null &&
                        signalmapdata.OutputChanged != null)
                    {
                        outputSignalData.signalAddressData.outputChanged -= signalmapdata.OutputChanged;
                    }
                    if (outputSignalData.signalAddressData?.outputChanged == null || outputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                        outputSignalData.signalAddressData.isBind = false;
                    break;
                case PLCSignalType.Truss:
                    if (outputSignalData.signalAddressData?.outputChanged != null &&
                        signalmapdata.OutputChanged != null)
                    {
                        outputSignalData.signalAddressData.outputChanged -= signalmapdata.OutputChanged;
                    }
                    if (outputSignalData.signalAddressData?.outputChanged == null || outputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                        outputSignalData.signalAddressData.isBind = false;
                    break;

                case PLCSignalType.PLC:
                    if (outputSignalData.plcData?.PlcOutValueAction != null &&
                        signalmapdata.PlcOutValueAction != null)
                    {
                        outputSignalData.plcData.PlcOutValueAction -= signalmapdata.PlcOutValueAction;
                    }
                    if (outputSignalData.plcData?.PlcOutValueAction == null || outputSignalData.plcData?.PlcOutValueAction.GetInvocationList().Length == 0)
                        outputSignalData.plcData.isBind = false;
                    break;

                case PLCSignalType.ExternalSignal:
                    if (outputSignalData.outPutBindData?.OutputSignalHandle != null &&
                        signalmapdata.OutputSignalHandle != null)
                    {
                        outputSignalData.outPutBindData.OutputSignalHandle -= signalmapdata.OutputSignalHandle;
                    }

                    //之前老的有监听
                    if (outputSignalData.outPutBindData?.OutputSignalHandle == null || outputSignalData.outPutBindData?.OutputSignalHandle.GetInvocationList().Length == 1)
                    {
                        outputSignalData.itemDetermineValue = 0;
                        foreach (var signalUI in outputSignalData.outPutBindData.signalConnectUiItems)
                        {
                            if (signalUI.itemIoType == outputSignalData.itemIoType
                                && signalUI.itemFieldName == outputSignalData.itemFieldName)
                            {
                                signalUI.itemDetermineValue = outputSignalData.itemDetermineValue;
                                break;
                            }
                        }
                    }


                    break;

                default:
                    break;
            }

            switch (inputSignalData.plcSignalType)
            {
                case PLCSignalType.Robot:
                    if (inputSignalData.signalAddressData?.inputChanged != null &&
                        signalmapdata.inputChanged != null)
                    {
                        inputSignalData.signalAddressData.inputChanged -= signalmapdata.inputChanged;
                    }
                    inputSignalData.signalAddressData.isBind = false;
                    break;
                case PLCSignalType.Truss:
                    if (inputSignalData.signalAddressData?.inputChanged != null &&
                        signalmapdata.inputChanged != null)
                    {
                        inputSignalData.signalAddressData.inputChanged -= signalmapdata.inputChanged;
                    }
                    inputSignalData.signalAddressData.isBind = false;
                    break;

                case PLCSignalType.PLC:
                    if (inputSignalData.plcData?.PlcIntValueAction != null &&
                        signalmapdata.PlcIntValueAction != null)
                    {
                        inputSignalData.plcData.PlcIntValueAction -= signalmapdata.PlcIntValueAction;
                    }
                    inputSignalData.plcData.isBind = false;

                    break;

                case PLCSignalType.ExternalSignal:
                    if (inputSignalData.outPutBindData?.InputSignalHandle != null &&
                        signalmapdata.InputSignalHandle != null)
                    {
                        inputSignalData.outPutBindData.InputSignalHandle -= signalmapdata.InputSignalHandle;
                    }

                    inputSignalData.itemDetermineValue = 0;
                    foreach (var signalUI in inputSignalData.outPutBindData.signalConnectUiItems)
                    {
                        if (signalUI.itemIoType == inputSignalData.itemIoType
                            && signalUI.itemFieldName == inputSignalData.itemFieldName)
                        {
                            signalUI.itemDetermineValue = inputSignalData.itemDetermineValue;
                            break;
                        }
                    }
                    break;

                default:
                    break;
            }
            SignalMapDatas.RemoveAt(index);
        }
        catch (Exception)
        {
            Debug.Log("删除失败!");
        }
    }
    public void RmoveDataFromMap(SignalMapItemData data)
    {
        if (SignalMapDatas.Contains(data))
        {
            SignalMapDatas.Remove(data);
        }
    }
    #endregion

    #region plc和机器人

    /// <summary>
    /// 删除plc或机器人的信号表
    /// </summary>
    /// <param name="pLCAddressData"></param>
    internal void RemovePLCAdressData(PLCAddressData pLCAddressData, SignalAddressData signalAddressData, bool isInputType = false)
    {
        if (pLCAddressData != null)
        {
            Debug.Log("删除映射");
            PLCAdressDataDeleteHandle?.Invoke(pLCAddressData, signalAddressData);
            SignalMapDatas.RemoveAll(c =>
            {
                if (c.OutputSignalData.plcSignalType == PLCSignalType.PLC && c.OutputSignalData?.plcData == pLCAddressData && !isInputType)
                {
                    switch (c.InputSignalData.plcSignalType)
                    {
                        case PLCSignalType.Robot:
                            if (c.InputSignalData.signalAddressData?.inputChanged != null &&
                                c.inputChanged != null)
                            {
                                c.InputSignalData.signalAddressData.inputChanged -= c.inputChanged;
                            }
                            c.InputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.Truss:
                            if (c.InputSignalData.signalAddressData?.inputChanged != null &&
                                c.inputChanged != null)
                            {
                                c.InputSignalData.signalAddressData.inputChanged -= c.inputChanged;
                            }
                            c.InputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.PLC:
                            if (c.InputSignalData.plcData?.PlcIntValueAction != null &&
                                c.PlcIntValueAction != null)
                            {
                                c.InputSignalData.plcData.PlcIntValueAction -= c.PlcIntValueAction;
                            }
                            c.InputSignalData.plcData.isBind = false;
                            break;
                        case PLCSignalType.ExternalSignal:
                            if (c.InputSignalData.outPutBindData?.InputSignalHandle != null &&
                                c.InputSignalHandle != null)
                            {
                                c.InputSignalData.outPutBindData.InputSignalHandle -= c.InputSignalHandle;
                            }

                            c.InputSignalData.itemDetermineValue = 0;
                            foreach (var signalUI in c.InputSignalData.outPutBindData.signalConnectUiItems)
                            {
                                if (signalUI.itemIoType == c.InputSignalData.itemIoType
                                    && signalUI.itemFieldName == c.InputSignalData.itemFieldName)
                                {
                                    signalUI.itemDetermineValue = c.InputSignalData.itemDetermineValue;
                                    break;
                                }
                            }
                            break;
                        default:
                            break;
                    }
                    if (c.OutputSignalData.plcData != null)
                    {
                        c.OutputSignalData.plcData.PlcIntValueAction = null;
                        c.OutputSignalData.plcData.PlcOutValueAction = null;
                    }

                    return true;
                }

                if (c.InputSignalData.plcSignalType == PLCSignalType.PLC && c.InputSignalData?.plcData == pLCAddressData)
                {
                    switch (c.OutputSignalData.plcSignalType)
                    {
                        case PLCSignalType.Robot:
                            if (c.OutputSignalData.signalAddressData?.outputChanged != null &&
                                c.OutputChanged != null)
                            {
                                c.OutputSignalData.signalAddressData.outputChanged -= c.OutputChanged;
                            }
                            if (c.OutputSignalData.signalAddressData?.outputChanged == null || c.OutputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                                c.OutputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.Truss:
                            if (c.OutputSignalData.signalAddressData?.outputChanged != null &&
                                c.OutputChanged != null)
                            {
                                c.OutputSignalData.signalAddressData.outputChanged -= c.OutputChanged;
                            }
                            if (c.OutputSignalData.signalAddressData?.outputChanged == null || c.OutputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                                c.OutputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.PLC:
                            if (c.OutputSignalData.plcData?.PlcOutValueAction != null &&
                                c.PlcOutValueAction != null)
                            {
                                c.OutputSignalData.plcData.PlcOutValueAction -= c.PlcOutValueAction;
                            }
                            if (c.OutputSignalData.plcData?.PlcOutValueAction == null || c.OutputSignalData.plcData?.PlcOutValueAction.GetInvocationList().Length == 0)
                                c.OutputSignalData.plcData.isBind = false;
                            break;
                        case PLCSignalType.ExternalSignal:
                            if (c.OutputSignalData.outPutBindData?.OutputSignalHandle != null &&
                                c.OutputSignalHandle != null)
                            {
                                c.OutputSignalData.outPutBindData.OutputSignalHandle -= c.OutputSignalHandle;
                            }

                            //之前老的有监听
                            if (c.OutputSignalData.outPutBindData?.OutputSignalHandle == null || c.OutputSignalData.outPutBindData?.OutputSignalHandle.GetInvocationList().Length == 1)
                            {
                                c.OutputSignalData.itemDetermineValue = 0;
                                foreach (var signalUI in c.OutputSignalData.outPutBindData.signalConnectUiItems)
                                {
                                    if (signalUI.itemIoType == c.OutputSignalData.itemIoType
                                        && signalUI.itemFieldName == c.OutputSignalData.itemFieldName)
                                    {
                                        signalUI.itemDetermineValue = c.OutputSignalData.itemDetermineValue;
                                        break;
                                    }
                                }
                            }
                            break;
                        default:
                            break;
                    }
                    if (c.InputSignalData.plcData != null)
                    {
                        c.InputSignalData.plcData.PlcIntValueAction = null;
                        c.InputSignalData.plcData.PlcOutValueAction = null;
                    }
                    return true;
                }
                return false;
            });
            pLCAddressData.isBind = false;
        }

        if (signalAddressData != null)
        {
            PLCAdressDataDeleteHandle?.Invoke(pLCAddressData, signalAddressData);
            SignalMapDatas.RemoveAll(c =>
            {
                if ((c.OutputSignalData.plcSignalType == PLCSignalType.Robot ||
                c.OutputSignalData.plcSignalType == PLCSignalType.Truss) &&
                c.OutputSignalData?.signalAddressData.index == signalAddressData.index &&
                c.OutputSignalData?.signalAddressData.iotype == signalAddressData.iotype &&
                c.OutputSignalData?.signalAddressData.plcname == signalAddressData.plcname && !isInputType
                )
                {
                    switch (c.InputSignalData.plcSignalType)
                    {
                        case PLCSignalType.Robot:
                            if (c.InputSignalData.signalAddressData?.inputChanged != null &&
                                c.inputChanged != null)
                            {
                                c.InputSignalData.signalAddressData.inputChanged -= c.inputChanged;
                            }
                            c.InputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.Truss:
                            if (c.InputSignalData.signalAddressData?.inputChanged != null &&
                                c.inputChanged != null)
                            {
                                c.InputSignalData.signalAddressData.inputChanged -= c.inputChanged;
                            }
                            c.InputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.PLC:
                            if (c.InputSignalData.plcData?.PlcIntValueAction != null &&
                                c.PlcIntValueAction != null)
                            {
                                c.InputSignalData.plcData.PlcIntValueAction -= c.PlcIntValueAction;
                            }
                            c.InputSignalData.plcData.isBind = false;
                            break;
                        case PLCSignalType.ExternalSignal:
                            if (c.InputSignalData.outPutBindData?.InputSignalHandle != null &&
                                c.InputSignalHandle != null)
                            {
                                c.InputSignalData.outPutBindData.InputSignalHandle -= c.InputSignalHandle;
                            }

                            c.InputSignalData.itemDetermineValue = 0;
                            foreach (var signalUI in c.InputSignalData.outPutBindData.signalConnectUiItems)
                            {
                                if (signalUI.itemIoType == c.InputSignalData.itemIoType
                                    && signalUI.itemFieldName == c.InputSignalData.itemFieldName)
                                {
                                    signalUI.itemDetermineValue = c.InputSignalData.itemDetermineValue;
                                    break;
                                }
                            }
                            break;
                        default:
                            break;
                    }

                    if (c.OutputSignalData.signalAddressData != null)
                    {
                        c.OutputSignalData.signalAddressData.inputChanged = null;
                        c.OutputSignalData.signalAddressData.outputChanged = null;
                    }
                    return true;
                }

                if ((c.InputSignalData.plcSignalType == PLCSignalType.Robot ||
                c.InputSignalData.plcSignalType == PLCSignalType.Truss)
                && c.InputSignalData?.signalAddressData.index == signalAddressData.index &&
                c.InputSignalData?.signalAddressData.iotype == signalAddressData.iotype &&
                c.InputSignalData?.signalAddressData.plcname == signalAddressData.plcname)
                {
                    switch (c.OutputSignalData.plcSignalType)
                    {
                        case PLCSignalType.Robot:
                            if (c.OutputSignalData.signalAddressData?.outputChanged != null &&
                                c.OutputChanged != null)
                            {
                                c.OutputSignalData.signalAddressData.outputChanged -= c.OutputChanged;
                            }
                            if (c.OutputSignalData.signalAddressData?.outputChanged == null || c.OutputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                                c.OutputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.Truss:
                            if (c.OutputSignalData.signalAddressData?.outputChanged != null &&
                                c.OutputChanged != null)
                            {
                                c.OutputSignalData.signalAddressData.outputChanged -= c.OutputChanged;
                            }
                            if (c.OutputSignalData.signalAddressData?.outputChanged == null || c.OutputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                                c.OutputSignalData.signalAddressData.isBind = false;
                            break;
                        case PLCSignalType.PLC:
                            if (c.OutputSignalData.plcData?.PlcOutValueAction != null &&
                                c.PlcOutValueAction != null)
                            {
                                c.OutputSignalData.plcData.PlcOutValueAction -= c.PlcOutValueAction;
                            }
                            if (c.OutputSignalData.plcData?.PlcOutValueAction == null || c.OutputSignalData.plcData?.PlcOutValueAction.GetInvocationList().Length == 0)
                                c.OutputSignalData.plcData.isBind = false;
                            break;
                        case PLCSignalType.ExternalSignal:
                            if (c.OutputSignalData.outPutBindData?.OutputSignalHandle != null &&
                                c.OutputSignalHandle != null)
                            {
                                c.OutputSignalData.outPutBindData.OutputSignalHandle -= c.OutputSignalHandle;
                            }

                            //之前老的有监听
                            if (c.OutputSignalData.outPutBindData?.OutputSignalHandle == null || c.OutputSignalData.outPutBindData?.OutputSignalHandle.GetInvocationList().Length == 1)
                            {
                                c.OutputSignalData.itemDetermineValue = 0;
                                foreach (var signalUI in c.OutputSignalData.outPutBindData.signalConnectUiItems)
                                {
                                    if (signalUI.itemIoType == c.OutputSignalData.itemIoType
                                        && signalUI.itemFieldName == c.OutputSignalData.itemFieldName)
                                    {
                                        signalUI.itemDetermineValue = c.OutputSignalData.itemDetermineValue;
                                        break;
                                    }
                                }
                            }
                            break;
                        default:
                            break;
                    }
                    if (c.OutputSignalData.signalAddressData != null)
                    {
                        c.OutputSignalData.signalAddressData.inputChanged = null;
                        c.OutputSignalData.signalAddressData.outputChanged = null;
                    }
                    return true;
                }
                return false;
            });
            signalAddressData.isBind = false;
        }

        MapDeleteHandle?.Invoke();
    }
    /// <summary>
    /// 移除Plc或者机器人设备
    /// </summary>
    /// <param name="plcSignaldata"></param>
    /// <param name="signalOutIptdata"></param>
    internal void RemovePLCSignalData(PLCSignalData plcSignaldata, SignalOutIptAct signalOutIptdata)
    {
        if (plcSignaldata != null)
        {
            foreach (var item in plcSignaldata.outputAddressDataForUI)
            {
                PLCAdressDataDeleteHandle?.Invoke(item, null);
                RemovePLCAdressData(item, null);
                SignalMapDatas.RemoveAll(c =>
                {
                    if (c.OutputSignalData.plcSignalType == PLCSignalType.PLC && c.OutputSignalData?.plcData == item)
                    {
                        c.OutputSignalData.plcData.PlcIntValueAction = null;
                        c.OutputSignalData.plcData.PlcOutValueAction = null;
                    }
                    if (c.InputSignalData.plcSignalType == PLCSignalType.PLC && c.InputSignalData?.plcData == item)
                    {
                        c.InputSignalData.plcData.PlcIntValueAction = null;
                        c.InputSignalData.plcData.PlcOutValueAction = null;
                    }
                    return true;
                });
            }
            foreach (var item in plcSignaldata.inputAddressDataForUI)
            {
                PLCAdressDataDeleteHandle?.Invoke(item, null);
                RemovePLCAdressData(item, null);
                SignalMapDatas.RemoveAll(c =>
                {
                    if (c.OutputSignalData.plcSignalType == PLCSignalType.PLC && c.OutputSignalData?.plcData == item)
                    {
                        c.OutputSignalData.plcData.PlcIntValueAction = null;
                        c.OutputSignalData.plcData.PlcOutValueAction = null;
                        return true;
                    }
                    if (c.InputSignalData.plcSignalType == PLCSignalType.PLC && c.InputSignalData?.plcData == item)
                    {
                        c.InputSignalData.plcData.PlcIntValueAction = null;
                        c.InputSignalData.plcData.PlcOutValueAction = null;
                        return true;
                    }
                    return false;
                });
            }
        }
        if (signalOutIptdata != null)
        {
            foreach (var item in signalOutIptdata.SignalIntAddress)
            {
                PLCAdressDataDeleteHandle?.Invoke(null, item);
                RemovePLCAdressData(null, item);
            }
            foreach (var item in signalOutIptdata.SignalOutAddress)
            {
                PLCAdressDataDeleteHandle?.Invoke(null, item);
                RemovePLCAdressData(null, item);
            }

            SignalMapDatas.RemoveAll(c =>
            {
                if ((c.OutputSignalData.plcSignalType == PLCSignalType.Robot || c.OutputSignalData.plcSignalType == PLCSignalType.Truss)
                && c.OutputSignalData?.signalOutIptAct == signalOutIptdata)
                {
                    c.OutputSignalData.signalAddressData.inputChanged = null;
                    c.OutputSignalData.signalAddressData.outputChanged = null;
                    return true;
                }
                if ((c.OutputSignalData.plcSignalType == PLCSignalType.Robot || c.OutputSignalData.plcSignalType == PLCSignalType.Truss)
                && c.InputSignalData?.signalOutIptAct == signalOutIptdata)
                {
                    c.InputSignalData.signalAddressData.inputChanged = null;
                    c.InputSignalData.signalAddressData.outputChanged = null;
                    return true;
                }
                return false;
            });
        }
    }

    #endregion

    #region OutPutBindata
    /// <summary>
    /// 移除outputBindata
    /// </summary>
    /// <param name="outdata"></param>
    public void RemoveOutBindata(OutPutBindData outputData, string itemFieldName = null, bool isInputType = false)
    {
        for (int i = SignalMapDatas.Count - 1; i >= 0; i--)
        {
            SignalMapItemData signalmapdata = SignalMapDatas[i];
            BehaviorIOItemData inputSignalData = signalmapdata.InputSignalData;
            BehaviorIOItemData outputSignalData = signalmapdata.OutputSignalData;
            if ((itemFieldName == null && ((signalmapdata.OutputSignalData.outPutBindData != null &&
                signalmapdata.OutputSignalData.outPutBindData.Id != null &&
                outputData != null &&
                outputData.Id != null &&
                signalmapdata.OutputSignalData.outPutBindData.Id == outputData.Id) ||
                (signalmapdata.InputSignalData.outPutBindData != null &&
                signalmapdata.InputSignalData.outPutBindData.Id != null &&
                outputData != null &&
                outputData.Id != null &&
                signalmapdata.InputSignalData.outPutBindData.Id == outputData.Id)))
                ||
                (itemFieldName != null && ((signalmapdata.OutputSignalData.outPutBindData != null &&
                signalmapdata.OutputSignalData.outPutBindData.Id != null &&
                outputData != null &&
                outputData.Id != null &&
                signalmapdata.OutputSignalData.outPutBindData.Id == outputData.Id &&
                signalmapdata.OutputSignalData.itemFieldName == itemFieldName) ||
                (signalmapdata.InputSignalData.outPutBindData != null &&
                signalmapdata.InputSignalData.outPutBindData.Id != null &&
                outputData != null &&
                outputData.Id != null &&
                signalmapdata.InputSignalData.outPutBindData.Id == outputData.Id &&
                signalmapdata.InputSignalData.itemFieldName == itemFieldName))
                ))
            {
                if (isInputType && !(signalmapdata.InputSignalData.outPutBindData != null &&
                signalmapdata.InputSignalData.outPutBindData.Id != null &&
                outputData != null &&
                outputData.Id != null &&
                signalmapdata.InputSignalData.outPutBindData.Id == outputData.Id &&
                signalmapdata.InputSignalData.itemFieldName == itemFieldName))
                    continue;
                switch (outputSignalData.plcSignalType)
                {
                    case PLCSignalType.Robot:
                        if (outputSignalData.signalAddressData?.outputChanged != null &&
                            signalmapdata.OutputChanged != null)
                        {
                            outputSignalData.signalAddressData.outputChanged -= signalmapdata.OutputChanged;
                        }
                        if (outputSignalData.signalAddressData?.outputChanged == null || outputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                            outputSignalData.signalAddressData.isBind = false;
                        break;
                    case PLCSignalType.Truss:
                        if (outputSignalData.signalAddressData?.outputChanged != null &&
                            signalmapdata.OutputChanged != null)
                        {
                            outputSignalData.signalAddressData.outputChanged -= signalmapdata.OutputChanged;
                        }
                        if (outputSignalData.signalAddressData?.outputChanged == null || outputSignalData.signalAddressData?.outputChanged.GetInvocationList().Length == 0)
                            outputSignalData.signalAddressData.isBind = false;
                        break;

                    case PLCSignalType.PLC:
                        if (outputSignalData.plcData?.PlcOutValueAction != null &&
                            signalmapdata.PlcOutValueAction != null)
                        {
                            outputSignalData.plcData.PlcOutValueAction -= signalmapdata.PlcOutValueAction;
                        }
                        if (outputSignalData.plcData?.PlcOutValueAction == null || outputSignalData.plcData?.PlcOutValueAction.GetInvocationList().Length == 0)
                            outputSignalData.plcData.isBind = false;
                        break;

                    case PLCSignalType.ExternalSignal:
                        if (outputSignalData.outPutBindData?.OutputSignalHandle != null &&
                            signalmapdata.OutputSignalHandle != null)
                        {
                            outputSignalData.outPutBindData.OutputSignalHandle -= signalmapdata.OutputSignalHandle;
                        }

                        //之前老的有监听
                        if (outputSignalData.outPutBindData?.OutputSignalHandle == null || outputSignalData.outPutBindData?.OutputSignalHandle.GetInvocationList().Length == 1)
                        {
                            outputSignalData.itemDetermineValue = 0;
                            foreach (var signalUI in outputSignalData.outPutBindData.signalConnectUiItems)
                            {
                                if (signalUI.itemIoType == outputSignalData.itemIoType
                                    && signalUI.itemFieldName == outputSignalData.itemFieldName)
                                {
                                    signalUI.itemDetermineValue = outputSignalData.itemDetermineValue;
                                    break;
                                }
                            }
                        }

                        break;
                    default:
                        break;
                }

                switch (inputSignalData.plcSignalType)
                {
                    case PLCSignalType.Robot:
                        if (inputSignalData.signalAddressData?.inputChanged != null &&
                            signalmapdata.inputChanged != null)
                        {
                            inputSignalData.signalAddressData.inputChanged -= signalmapdata.inputChanged;
                        }
                        inputSignalData.signalAddressData.isBind = false;
                        break;
                    case PLCSignalType.Truss:
                        if (inputSignalData.signalAddressData?.inputChanged != null &&
                            signalmapdata.inputChanged != null)
                        {
                            inputSignalData.signalAddressData.inputChanged -= signalmapdata.inputChanged;
                        }
                        inputSignalData.signalAddressData.isBind = false;
                        break;

                    case PLCSignalType.PLC:
                        if (inputSignalData.plcData?.PlcIntValueAction != null &&
                            signalmapdata.PlcIntValueAction != null)
                        {
                            inputSignalData.plcData.PlcIntValueAction -= signalmapdata.PlcIntValueAction;
                        }
                        inputSignalData.plcData.isBind = false;

                        break;

                    case PLCSignalType.ExternalSignal:
                        if (inputSignalData.outPutBindData?.InputSignalHandle != null &&
                            signalmapdata.InputSignalHandle != null)
                        {
                            inputSignalData.outPutBindData.InputSignalHandle -= signalmapdata.InputSignalHandle;
                        }

                        inputSignalData.itemDetermineValue = 0;
                        foreach (var signalUI in inputSignalData.outPutBindData.signalConnectUiItems)
                        {
                            if (signalUI.itemIoType == inputSignalData.itemIoType
                                && signalUI.itemFieldName == inputSignalData.itemFieldName)
                            {
                                signalUI.itemDetermineValue = inputSignalData.itemDetermineValue;
                                break;
                            }
                        }
                        break;

                    default:
                        break;
                }

                SignalMapDatas.RemoveAt(i);
                MapDeleteHandle?.Invoke();
            }
        }
    }
    #endregion
}
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Battlehub;
using Battlehub.RTCommon;
using I18N.Common;
using SignalMapping;
using UnityEngine;
[Serializable]

public class OutPutBindData : MonoBehaviour
{
    SignalOutIptAct signal;
    OutPutBindData outPut;
    public Action<int, bool> magnetismChangedAct;
    public Action<BehaviorIOItemData, string, bool, List<bool>> InputSignalHandle;
    public Action<BehaviorIOItemData, string, bool, List<bool>> OutputSignalHandle;
    [SerializeIgnore]
    public List<string> FalseType;

    [SerializeField, SerializeIgnore]
    public OData ioDta = new OData();
    [SerializeIgnore]

    public ModelType _parentModelTYpe;
    [SerializeIgnore]
    private ModelGameobjectBase _model;
    [SerializeIgnore]
    private ModelGameobjectBase _modelpar;
    [SerializeIgnore]
    public BindType bindType;
    [SerializeIgnore]
    public bool Playstat;
    [SerializeIgnore]
    private SceneModelsManager modelManager;
    [SerializeIgnore]
    public SceneModelsManager sceneModelsManager
    {
        get
        {
            if (modelManager == null)
            {
                modelManager = SceneModelsManager.GetInstance();
            }
            return modelManager;
        }
    }
    [SerializeIgnore]
    public DataManager manager;
    [SerializeIgnore]
    public DataManager dataManager
    {
        get
        {
            if (manager == null)
            {
                manager = DataManager.GetInstance();
            }
            return manager;
        }
    }
    [SerializeIgnore]
    public EventManager eventman;
    [SerializeIgnore]
    public EventManager eventManager
    {
        get
        {
            if (eventman == null)
            {
                eventman = EventManager.GetInstance();
            }
            return eventman;
        }
    }
    protected IRTE m_editor;
    private string modelname;
    public string Id = "";
    public Action<string> NameChange;
    protected string mId = null;

    public List<BehaviorIOItemData> signalConnectUiItems = new List<BehaviorIOItemData>();

    public Action<OutPutBindData> DeleteOutPutDataGo;
    [SerializeIgnore, HideInInspector]
    public bool isBehaviouralOrAGV = false;

    public void AddItemToData(BehaviorIOItemData data)
    {
        data.outPutBindDataId = Id;
        if (data.itemIsEnum && data.EnumName != null && !data.itemIsEnumChild)
        {
            Type enumType = Type.GetType(data.EnumName);
            if (enumType != null && enumType.IsEnum)
            {
                Array enumValues = Enum.GetValues(enumType);

                AddItemToData
                   (
                       new BehaviorIOItemData()
                       {
                           outPutBindData = data.outPutBindData,
                           itemName = data.itemName,
                           itemFieldName = data.itemFieldName,
                           itemIsEnum = true,
                           itemIsEnumChild = true,
                           //   itemEnumChildName = enumValues[0],
                           EnumName = data.EnumName,
                           itemIoType = data.itemIoType,
                           itemDataType = data.itemDataType,
                           itemDetermineValue = data.itemDetermineValue,
                           bindDataType = data.bindDataType,
                           outPutBindDataId = Id,
                           plcSignalType = PLCSignalType.ExternalSignal
                       }
                   ); ;
            }
            else
            {
                Console.WriteLine("Failed to get Enum type.");
            }

        }
        else
        {
            signalConnectUiItems.Add(data);
            //NameChange += data.ChangeName;
        }


    }
    public void RefreshGoNameToSignal()
    {
        //枚举类型的同�?
        //if(signalConnectUiItems.Count>0)
        //{
        //    foreach (var item in signalConnectUiItems)
        //    {
        //        if(item.itemIsEnum && item.EnumName != null)
        //        {
        //            string originalString = item.itemName;
        //            string replacementString = gameObject.name;

        //            int lastIndex = originalString.LastIndexOf("_"); 

        //            if (lastIndex != -1)
        //            {
        //                item.itemName = replacementString + originalString.Substring(lastIndex);
        //            }
        //            else
        //            {
        //                Console.WriteLine("字符串中没有下划�?); 
        //            }
        //        }
        //        else
        //        {
        //            item.itemName = gameObject.name;
        //        }
        //    }
        //}
    }

    protected virtual void OnEnable()
    {
        sceneModelsManager.AddoutData(this);
    }

    private void EventName_UpdateIKDataEvent(object[] args)
    {
        magnetismFunction();
    }

    protected virtual void Start()
    {
        InputSignalHandle += PLCOutMethod;
        OutputSignalHandle += PLCInMethod;
        // SceneModelsManager.GetInstance().AddoutData(this);
        FalseType = new List<string>() { "Null" };
        ImportOrExportProjectManager.Instance.ImportProjectCompleteAction += InitMapAction;
        BehaviorlViewModel bv = FindObjectOfType<BehaviorlViewModel>();
        if (bv != null)
        {
            bv.AddObjectRefresh(this);
        }

        m_editor = IOC.Resolve<IRTE>();
        m_editor.ObjectsDeleted += OnObjectDeleted;

        Globals.EventManager.Regist(EventManager.EventName_UpdateIKData, EventName_UpdateIKDataEvent);
        ioDta.oid = Id;

        if (!isBehaviouralOrAGV)
        {
            signalConnectUiItems = new List<BehaviorIOItemData>();
        }
        else
        {
            if (signalConnectUiItems?.Count > 0)
            {
                foreach (var item in signalConnectUiItems)
                {
                    item.plcSignalType = PLCSignalType.ExternalSignal;
                }
            }
        }

        m_editor.Object.NameChanged += OnObjectNameChanged;
        outPut = this.GetComponent<OutPutBindData>();
        Globals.EventManager.Regist(EventManager.EventName_AssemblySucceeded, AssemblySucceededEvent);
    }

    private void OnObjectNameChanged(ExposeToEditor obj)
    {
        if (obj == null) return;
        if (obj.gameObject == this.gameObject)
        {
            UpadateName();
        }
    }
    private void UpadateName()
    {
        for (int i = 0; i < signalConnectUiItems.Count; i++)
        {
            signalConnectUiItems[i].itemName = gameObject.name;
        }

        foreach (var item in SignaMapDataController.Instance.GetSignalMapDatas())
        {
            if (item.OutputSignalData.outPutBindDataId == Id)
            {
                item.OutputSignalData.itemName = gameObject.name;
            }
            if (item.InputSignalData.outPutBindDataId == Id)
            {
                item.InputSignalData.itemName = gameObject.name;
            }
        }
    }

    private void InitMapAction()
    {
        if (signalConnectUiItems != null && signalConnectUiItems.Count > 0)
        {
            foreach (var item in signalConnectUiItems)
            {
                item.outPutBindData = this;
            }
        }
    }

    public void OnIdChangUpdateSignalData()
    {
        if (signalConnectUiItems == null) return;
        foreach (var item in signalConnectUiItems)
        {
            if (item.outPutBindData != null)
                item.outPutBindDataId = item.outPutBindData.Id;
        }
    }

    private void OnObjectDeleted(GameObject[] games)
    {
        for (int i = 0; i < games.Length; i++)
        {
            //DeleteOutPut(games[i].GetComponentsInChildren<OutPutBindData>());
        }
    }

    protected virtual void Update()
    {
        if ((mId != Id || Id == "" || Id == null) && dataManager != null)
        {
            mId = Id = dataManager.UpdateDevice(this, Id);
            if (ioDta != null)
                ioDta.oid = Id;

            OnIdChangUpdateSignalData();

            eventManager.DispatchEvent(EventManager.EventName_UpdateIOBindData);
            Globals.EventManager.DispatchEvent(EventManager.EventName_UpdateIOCurrent);
        }
        if (gameObject.name != modelname && eventManager != null)
        {
            modelname = gameObject.name;
            eventManager.DispatchEvent(EventManager.EventName_UpdateIOBindData);
            Globals.EventManager.DispatchEvent(EventManager.EventName_UpdateIOCurrent);
        }

        _model = GetComponent<ModelGameobjectBase>();
        _modelpar = GetComponentInParent<ModelGameobjectBase>();
        if (_modelpar != null || _model != null)
        {
            if (_model)
            {
                _parentModelTYpe = _model.type;
            }
            else
            {
                _parentModelTYpe = _modelpar.type;
            }
        }
        else
        {
            _parentModelTYpe = ModelType.None;
        }

        if (ioDta != null)
        {
            if (!ioDta.ikgm && (ioDta.ikid != "" && ioDta.ikid != null))
            {
                ioDta.ikgm = dataManager.GetGameObject(ioDta.ikid);
            }
            if (!ioDta.ogm && (ioDta.oid != "" && ioDta.oid != null))
            {
                ioDta.ogm = dataManager.GetGameObject(ioDta.oid);
            }
            if (ioDta.ikid != "" && ioDta.oid != "" && ioDta.truestr != "" && ioDta.ikgm)
            {
                if (ioDta.ikgm.TryGetComponent<SignalOutIptAct>(out SignalOutIptAct signalOutIpt))
                {
                    if (!signalOutIpt.oDatasDic.ContainsKey(ioDta))
                    {
                        signalOutIpt.oDatasDic.Add(ioDta, "delect");
                        if (ioDta.ogm.TryGetComponent<OutPutBindData>(out OutPutBindData MagnetCon))
                        {
                            signalOutIpt.outputChanged += MagnetCon.magnetismChanged;
                        }
                    }
                }
                //if (PLCSignalData)
                //{

                //}
            }
        }
    }
    protected int GetDataTypeSizeInBits(string datatype)
    {
        switch (datatype)
        {
            case "Bool":
                return 1;
            case "Byte":
                return 8;
            case "Int16":
                return 16;
            case "Float":
                return 32;
            case "Int64":
                return 64;
            default:
                return 0;
        }
    }

    

    // 虚方法，子类可以重写以提供不同的数据类型
    protected virtual string GetDefaultDataType()
    {
        return "Bool"; // 默认数据类型
    }

    public void AssemblySucceededEvent(object[] args)
    {
        Debug.Log("装配就发消息各自确认下");
        bool isAssembly = false;

        if (args != null && args.Length >= 2)
        {
            if (args[1] == null && !GetComponentInParent<SignalOutIptAct>())
            {
                for (int i = SignaMapDataController.Instance.SignalMapDatas.Count - 1; i >= 0; i--)
                {
                    var item = SignaMapDataController.Instance.SignalMapDatas[i];
                    if (item?.InputSignalData?.outPutBindData == this)
                    {
                        SignaMapDataController.Instance.SignalMapDatas.RemoveAt(i);
                    }
                }
            }
        }
        var output = GetComponentsInChildren<OutPutBindData>();
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] ==this)
            {
                //替换的逻辑执行
            }
        }
        //正常装配的逻辑执行
        // 查找最大的索引
        if (SignaMapDataController.Instance.SignalMapDatas.Any(x => x.InputSignalData?.outPutBindData != null && x.InputSignalData.outPutBindData.Equals(this)))
        {
            return;
        }
        var signal = GetComponentInParent<SignalOutIptAct>();
        if (!signal)
        {
            return;
        }

        string dataType = GetDefaultDataType();
        int dataTypeSize = GetDataTypeSizeInBits(dataType);

        // 检查总位宽是否超过限制
        if (!CheckTotalBitWidthWithinLimit(signal, dataTypeSize))
        {
            Debug.LogWarning($"无法添加新地址: 总位宽超过128位限制");
            return;
        }

        // 查找最大的索引
        SignalAddressData maxIndexItem = signal.SignalOutAddress
            .OrderByDescending(x => x.index)
            .FirstOrDefault();

        int newIndex = 0; // 默认从0开始

        // 如果有现有数据，计算新索引
        if (maxIndexItem != null && !string.IsNullOrEmpty(maxIndexItem.datatype))
        {
            newIndex = maxIndexItem.index + GetDataTypeSizeInBits(maxIndexItem.datatype);
        }

        // 检查单个地址的结束位置是否超过128位
        if (!CheckTotalBitWidthWithinLimit(signal, dataTypeSize))
        {
            Debug.LogWarning($"无法添加新地址: 索引{newIndex}加上数据类型{dataType}({dataTypeSize}位)超过128位限制");
            return;
        }

        SignalAddressData newAddress = new SignalAddressData()
        {
            plcname = gameObject.name, // 使用虚方法获取默认名称
            iotype = "Output",
            datatype = dataType,  // 使用获取的数据类型
            index = newIndex,
            isBind = false
        };

        signal.SignalOutAddress.Add(newAddress);

        BehaviorIOItemData itemdata = new BehaviorIOItemData()
        {
            itemName = newAddress.plcname,
            itemIoType = newAddress.iotype,
            itemDataType = newAddress.datatype,
            itemDetermineValue = newAddress.isBind ? 1 : 0,
            isRobotOrTruss = true,
            signalAddressData = newAddress,
            signalOutIptAct = signal,
            signalOutDataId = signal.Id,
            plcSignalType = PLCSignalType.Robot
        };

        // 确保outPut.signalConnectUiItems不为空且至少有一个元素
        if (outPut != null && outPut.signalConnectUiItems != null && outPut.signalConnectUiItems.Count > 0)
        {
            SignaMapDataController.Instance.InsertMapData(outPut.signalConnectUiItems[0], itemdata);
        }
    }

    // 检查所有地址的总位宽是否超过128位
    private bool CheckTotalBitWidthWithinLimit(SignalOutIptAct signal, int newDataTypeSize)
    {
        if (signal == null || signal.SignalOutAddress == null)
        {
            return true; // 如果没有地址，肯定在限制内
        }

        // 计算现有地址的总位宽
        int totalBits = 0;
        foreach (var address in signal.SignalOutAddress)
        {
            if (!string.IsNullOrEmpty(address.datatype))
            {
                totalBits += GetDataTypeSizeInBits(address.datatype);
            }
        }

        // 加上新地址的位宽
        totalBits += newDataTypeSize;

        // 检查是否超过128位
        if (totalBits > 128)
        {
            Debug.LogWarning($"总位宽超过128位限制: 现有{totalBits - newDataTypeSize}位 + 新增{newDataTypeSize}位 = {totalBits}位");
            return false;
        }

        return true;
    }

    // 或者你也可以提供一个更详细的检查方法，可以在添加前检查
    public bool CanAddNewAddress()
    {
        var signal = GetComponentInParent<SignalOutIptAct>();
        if (!signal)
        {
            return false;
        }

        SignalAddressData maxIndexItem = signal.SignalOutAddress
            .OrderByDescending(x => x.index)
            .FirstOrDefault();

        int newIndex = 0;
        string dataType = GetDefaultDataType();
        int dataTypeSize = GetDataTypeSizeInBits(dataType);

        if (maxIndexItem != null && !string.IsNullOrEmpty(maxIndexItem.datatype))
        {
            newIndex = maxIndexItem.index + GetDataTypeSizeInBits(maxIndexItem.datatype);
        }

        return CheckTotalBitWidthWithinLimit(signal, dataTypeSize);
    }
    // PLCOutMethodAct?.Invoke(data, arg1, arg2, arg3);
    public virtual void PLCOutMethod(BehaviorIOItemData data, string arg1, bool arg2, List<bool> arg3)
    {

    }
    public virtual void PLCInMethod(BehaviorIOItemData data, string arg1, bool arg2, List<bool> arg3)
    {

    }
    public void TriggerPLCInHandle(string itemFieldName, string strValue, bool boolValue, List<bool> listBoolValue)
    {
        if (UtilsLogic.FindObjectsOfType<SignaMapDataController>().Length == 0) return;
        foreach (var item in SignaMapDataController.Instance.GetSignalMapDatas())
        {
            if (item.OutputSignalData == null) continue;
            if (item.OutputSignalData.plcSignalType != PLCSignalType.ExternalSignal || item.OutputSignalData.outPutBindDataId != Id || (item.OutputSignalData.itemFieldName != itemFieldName)) continue;
            OutputSignalHandle?.Invoke(item.OutputSignalData, strValue, boolValue, listBoolValue);
        }
    }
    public virtual void SignalInputChange(BehaviorIOItemData data, int index, bool value)
    {

    }
    public virtual void SignalOutputChange(BehaviorIOItemData data, int index, bool value)
    {

    }
    public virtual void magnetismChanged(int index, bool flag)
    {
        magnetismFunction();
        magnetismChangedAct?.Invoke(index, flag);
    }
    private void magnetismFunction()
    {
        if (ioDta.ikgm)
        {
            if (ioDta.ikgm.TryGetComponent<SignalOutIptAct>(out SignalOutIptAct signalOutIpt))
            {
                Playstat = signalOutIpt.Output[ioDta.startint];
            }
        }
    }
    public virtual void Revermagnetism(bool istrue)
    {
        if (ioDta.ikgm)
        {
            if (ioDta.ikgm.TryGetComponent<SignalOutIptAct>(out SignalOutIptAct signalOutIpt))
            {
                Playstat = signalOutIpt.Output[ioDta.startint] = istrue;
            }
        }
    }

    protected virtual void OnDisable()
    {
        sceneModelsManager.RemoveoutData(this);

        //SignaMapDataController.Instance.PLCAdressDataDeleteHandle -= RemoveSignalMapData;
        DeleteOutPutDataGo?.Invoke(this);
        DeleteOutPutDataGo = null;
        if (m_editor != null && m_editor.Object != null)
            m_editor.Object.NameChanged -= OnObjectNameChanged;
    }
    protected virtual void OnDestroy()
    {
        if (m_editor != null)
        {
            m_editor.ObjectsDeleted -= OnObjectDeleted;
            if (m_editor.Object != null)
                m_editor.Object.NameChanged -= OnObjectNameChanged;
        }
        Globals.EventManager.UnRegist(EventManager.EventName_UpdateIKData, EventName_UpdateIKDataEvent);
        ImportOrExportProjectManager.Instance.ImportProjectCompleteAction -= InitMapAction;
        Globals.EventManager.UnRegist(EventManager.EventName_AssemblySucceeded, AssemblySucceededEvent);
        InputSignalHandle -= PLCOutMethod;
        OutputSignalHandle -= PLCInMethod;

        if (UtilsLogic.FindObjectsOfType<SignaMapDataController>().Length > 0)
        {
            try
            {
                SignaMapDataController.Instance.DeleteOutPut(GetComponentsInChildren<OutPutBindData>());
            }
            catch (Exception)
            {
            }

        }
    }
}


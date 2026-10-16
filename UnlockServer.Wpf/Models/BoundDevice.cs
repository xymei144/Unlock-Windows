using System;
using System.Runtime.Serialization;
using UnlockServer.Services;
using UnlockServer.ViewModels;

namespace UnlockServer.Models
{
    [DataContract]
    public class BoundDevice : ViewModelBase
    {
        private string _name;
        private string _address;
        private int _bluetoothType = 2;
        private bool _enabled = true;
        private short _rssi = -100;
        private bool _isInRange;
        private string _statusText = LangService.T("T.Dev.Status.Waiting");
        private string _irk;

        /// <summary>
        /// 苹果设备的身份解析密钥（IRK，32 位十六进制，可选）。
        /// 一般不用填：地址轮换后软件靠广播名就能认回设备。
        /// 仅当「地址和广播名同时变化」导致识别失效时，才手工编辑 devices.json 填入。
        /// </summary>
        [DataMember]
        public string Irk
        {
            get => _irk;
            set => SetProperty(ref _irk, value);
        }

        [IgnoreDataMember]
        public string AddressKind => RpaResolver.DescribeAddress(Address);

        [DataMember]
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }

        [DataMember]
        public string Address
        {
            get => _address;
            set => SetProperty(ref _address, value);
        }

        [DataMember]
        public int BluetoothType
        {
            get => _bluetoothType;
            set
            {
                if (SetProperty(ref _bluetoothType, value))
                    OnPropertyChanged(nameof(TypeLabel));
            }
        }

        [DataMember]
        public bool Enabled
        {
            get => _enabled;
            set => SetProperty(ref _enabled, value);
        }

        public short Rssi
        {
            get => _rssi;
            set
            {
                if (SetProperty(ref _rssi, value))
                    OnPropertyChanged(nameof(RssiDisplay));
            }
        }

        public bool IsInRange
        {
            get => _isInRange;
            set => SetProperty(ref _isInRange, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string DisplayName => string.IsNullOrWhiteSpace(Name)
            ? (string.IsNullOrWhiteSpace(Address) ? LangService.T("T.Dev.Unnamed") : Address)
            : Name;

        public string TypeLabel => LangService.T(BluetoothType == 1 ? "T.Type.Classic" : "T.Type.Ble");

        public string RssiDisplay => Rssi > -100 ? $"{Rssi} dBm" : "--";

        public string NormalizedAddress => BluetoothDiscover.NormalizeAddress(Address) ?? Address;

        /// <summary>语言切换后刷新由翻译生成的展示字段</summary>
        public void RefreshLocalized()
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(TypeLabel));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}

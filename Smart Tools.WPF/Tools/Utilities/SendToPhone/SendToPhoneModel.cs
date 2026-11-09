using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>How the transfer is protected. Both use HTTPS; Encrypted adds end-to-end encryption on top.</summary>
    public enum PhoneSecurity
    {
        /// <summary>HTTPS plus end-to-end encryption with a key that only the QR code carries.</summary>
        Encrypted,
        /// <summary>HTTPS only: faster, and large downloads stream straight to the phone.</summary>
        TlsOnly
    }

    public enum PhoneState
    {
        Stopped,
        Starting,
        WaitingForPhone,
        WaitingForApproval,
        Connected
    }

    public enum PhoneDirection
    {
        ToPhone,
        FromPhone
    }

    public enum PhoneTransferState
    {
        Waiting,
        Active,
        Done,
        Failed
    }

    /// <summary>What the tool remembers: how it is set up, never which files were sent or which folder was used.</summary>
    public sealed class PhoneSettings
    {
        public PhoneSecurity Security { get; set; } = PhoneSecurity.Encrypted;
        public string InterfaceName { get; set; } = "";
        /// <summary>Largest file accepted from the phone, in MB; 0 = no limit (the free space still decides).</summary>
        public int MaxFileMb { get; set; } = 2048;
        /// <summary>The page closes by itself after this many minutes without anything happening.</summary>
        public int IdleMinutes { get; set; } = 15;
        public bool BlockPrograms { get; set; } = true;
    }

    /// <summary>One thing on its way, either direction. The view reads it a few times a second; the server updates it.</summary>
    public sealed class PhoneTransfer : INotifyPropertyChanged
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public PhoneDirection Direction { get; init; }
        public bool IsText { get; init; }
        public long Size { get; set; }

        private long _done;
        public long Done { get => Interlocked.Read(ref _done); set => Interlocked.Exchange(ref _done, value); }

        public volatile PhoneTransferState State;

        /// <summary>The text that arrived (a text from the phone) or was offered (a text for the phone).</summary>
        public string? Text { get; set; }

        /// <summary>Where a received file was saved.</summary>
        public string? SavedPath { get; set; }

        /// <summary>Why it failed, in words for the user (already localised).</summary>
        public string? Problem { get; set; }

        public double Percent => Size <= 0 ? (State == PhoneTransferState.Done ? 100 : 0) : Math.Min(100, Done * 100.0 / Size);

        // What the list shows (the view binds to these; Notify() refreshes them).
        public bool IsToPhone => Direction == PhoneDirection.ToPhone;
        public bool IsActive => State is PhoneTransferState.Active or PhoneTransferState.Waiting;
        public bool IsFailed => State == PhoneTransferState.Failed;
        public bool CanShowFolder => SavedPath != null && State == PhoneTransferState.Done;
        public bool CanCopyText => IsText && Text != null && State == PhoneTransferState.Done;
        public string SizeText => IsText ? "" : FormatSize(Size);

        public string StatusText => State switch
        {
            PhoneTransferState.Waiting => Loc.T("Str_SendToPhone_StWaiting", "Waiting..."),
            PhoneTransferState.Active => $"{Percent:0}% · {FormatSize(Done)}",
            PhoneTransferState.Done => IsText
                ? Loc.T("Str_SendToPhone_StText", "Text received")
                : (Direction == PhoneDirection.ToPhone ? Loc.T("Str_SendToPhone_StSent", "Sent to the phone") : Loc.T("Str_SendToPhone_StReceived", "Received")),
            _ => Problem ?? Loc.T("Str_SendToPhone_StFailed", "Failed"),
        };

        public static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
            _ => $"{bytes / 1073741824.0:0.##} GB",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The view calls this on a timer (progress changes far more often than the screen needs).</summary>
        public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>A file or text the PC offers to the phone.</summary>
    public sealed class PhoneOffer
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Path { get; init; }
        public string? Text { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public bool IsText => Text != null;
    }
}

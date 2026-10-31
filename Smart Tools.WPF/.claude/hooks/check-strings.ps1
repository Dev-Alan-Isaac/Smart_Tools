# PostToolUse hook (Edit|Write): checks user-facing text against the "Rules made by the author" in CLAUDE.md.
# Only Language/en.xaml and Language/es.xaml are inspected. Style checks (en.xaml) look at the text just
# written, so old strings never flood the report; key parity compares the two whole files.
# Reads the hook JSON on stdin; prints JSON with additionalContext when something is wrong, else nothing.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $file = [string]$payload.tool_input.file_path
    if (-not $file) { exit 0 }
    $leaf = Split-Path $file -Leaf
    $inLanguageDir = (Split-Path (Split-Path $file -Parent) -Leaf) -eq 'Language'
    if (-not $inLanguageDir -or $leaf -notin @('en.xaml', 'es.xaml')) { exit 0 }

    $issues = New-Object System.Collections.Generic.List[string]
    $keyPattern = 'x:Key="(Str_[^"]+)"'

    # --- key parity (whole files) ---
    $dir = Split-Path $file -Parent
    $en = Join-Path $dir 'en.xaml'
    $es = Join-Path $dir 'es.xaml'
    if ((Test-Path $en) -and (Test-Path $es)) {
        $enKeys = [Regex]::Matches([IO.File]::ReadAllText($en), $keyPattern) | ForEach-Object { $_.Groups[1].Value }
        $esKeys = [Regex]::Matches([IO.File]::ReadAllText($es), $keyPattern) | ForEach-Object { $_.Groups[1].Value }
        $missingInEs = @($enKeys | Where-Object { $_ -notin $esKeys })
        $missingInEn = @($esKeys | Where-Object { $_ -notin $enKeys })
        if ($missingInEs.Count) { $issues.Add("Missing in es.xaml ($($missingInEs.Count)): " + (($missingInEs | Select-Object -First 8) -join ', ')) }
        if ($missingInEn.Count) { $issues.Add("Missing in en.xaml ($($missingInEn.Count)): " + (($missingInEn | Select-Object -First 8) -join ', ')) }
    }

    # --- style rules (en.xaml only, on the text just written) ---
    if ($leaf -eq 'en.xaml') {
        $written = if ($payload.tool_input.new_string) { [string]$payload.tool_input.new_string } else { [string]$payload.tool_input.content }
        $british = 'colour|centre|cancell|behaviour|favourite|grey|organis|optimis|analys|licence|catalogue|metre\b|travell|labell|modell|initialis|customis|normalis|recognis|summaris|visualis|synchronis|honour|neighbour|programme\b|defence|offence|whilst|ageing|artefact|minimis|maximis|utilis|categoris|prioritis|authoris|serialis|finalis|emphasis(e|ing|ed)\b'
        $small = 'a|an|the|of|and|or|to|in|on|at|by|for|with|from|as|per|vs|via|is|are|be|into|off|up'
        $stringPattern = '<system:String\s+x:Key="(?<key>Str_[^"]+)"[^>]*>(?<text>[^<]*)</system:String>'
        foreach ($m in [Regex]::Matches($written, $stringPattern)) {
            $key = $m.Groups['key'].Value
            $text = [Net.WebUtility]::HtmlDecode($m.Groups['text'].Value)

            if ($text -match "(?i)\b($british)") { $issues.Add("$key - British spelling ('$($Matches[1])...'): use American English: `"$text`"") }

            # Title Case: 3+ words, every non-small word capitalized, 2+ of them after the first word.
            $words = [Regex]::Matches($text, "[A-Za-z][A-Za-z'-]+") | ForEach-Object { $_.Value }
            if ($words.Count -ge 3 -and $text -notmatch '[.!?]$') {
                $content = @($words | Select-Object -Skip 1 | Where-Object { $_ -notmatch "^($small)$" })
                $caps = @($content | Where-Object { $_ -cmatch '^[A-Z][a-z]' })
                $acronyms = @($content | Where-Object { $_ -cmatch '^[A-Z]{2,}$' })
                if ($content.Count -ge 2 -and $caps.Count -eq $content.Count -and $acronyms.Count -eq 0) { $issues.Add("$key - looks like Title Case, use sentence case: `"$text`"") }
            }

            # Oxford comma: "a, b and c" / "a, b or c" with single-word items.
            if ($text -match '\b\w+, \w+ (and|or) \w+\b') { $issues.Add("$key - missing Oxford comma before '$($Matches[1])': `"$text`"") }
        }
    }

    if ($issues.Count -eq 0) { exit 0 }

    $header = "Language file check ($leaf) found $($issues.Count) issue(s) against the CLAUDE.md author rules (American English, sentence case, Oxford comma, en/es key parity). Fix them now; if one is a false positive (for example a proper name), ignore it:"
    $context = $header + "`n- " + (($issues | Select-Object -First 25) -join "`n- ")
    @{ hookSpecificOutput = @{ hookEventName = 'PostToolUse'; additionalContext = $context } } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}
catch {
    # A broken check must never block editing; surface the problem quietly.
    [Console]::Error.WriteLine("check-strings.ps1: $($_.Exception.Message)")
    exit 0
}

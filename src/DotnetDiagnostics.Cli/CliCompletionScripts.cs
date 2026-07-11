using System.Text;

namespace DotnetDiagnostics.Cli;

internal static class CliCompletionScripts
{
    public static readonly IReadOnlyList<string> Shells = CliCommandCatalog.Shells;

    /// <summary>
    /// Every long option flag (<c>--foo</c>) advertised by the completion catalog — both the global
    /// options and the per-command option lists — de-duplicated. Exposed for the doc-parity guardrail
    /// so completion can never advertise a flag that is absent from <c>docs/cli-reference.md</c>.
    /// </summary>
    internal static IReadOnlyList<string> AllCommandOptionFlags =>
        CliCommandCatalog.GlobalOptions
            .Concat(CliCommandCatalog.CommandDescriptors.SelectMany(static descriptor => descriptor.CompletionOptions))
            .Where(static flag => flag.StartsWith("--", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static flag => flag, StringComparer.Ordinal)
            .ToArray();

    public static string ForShell(string shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        return shell switch
        {
            "bash" => Bash(),
            "zsh" => Zsh(),
            "pwsh" => Pwsh(),
            _ => throw new ArgumentException($"Unknown completion shell '{shell}'.", nameof(shell)),
        };
    }

    private static string Bash()
    {
        var commands = BashWords(CliCommandCatalog.CommandNames);
        var globalOptions = BashWords(CliCommandCatalog.GlobalOptions);
        var collectKinds = BashWords(CliCommands.CollectKinds);
        var heapSources = BashWords(CliCommands.HeapSources);
        var byteKinds = BashWords(CliCommands.ByteKinds);
        var byteAssets = BashWords(CliCommands.ByteAssets);
        var dumpTypes = BashWords(CliCommands.DumpTypes);
        var shells = BashWords(Shells);
        var valueFlags = BashWords(CliCommandCatalog.ValueFlags);

        return $$"""
        # bash completion for dotnet-diagnostics.
        _dotnet_diagnostics_complete()
        {
            local cur prev command opts
            COMPREPLY=()
            cur="${COMP_WORDS[COMP_CWORD]}"
            prev="${COMP_WORDS[COMP_CWORD-1]}"
            command=""
            local value_flags="{{valueFlags}}"
            local skip_next=0
            for ((i = 1; i < COMP_CWORD; i++)); do
                local word="${COMP_WORDS[i]}"
                if [[ $skip_next -eq 1 ]]; then
                    skip_next=0
                    continue
                fi
                if [[ " $value_flags " == *" $word "* ]]; then
                    skip_next=1
                    continue
                fi
                if [[ "$word" != -* ]]; then
                    command="$word"
                    break
                fi
            done

            case "$prev" in
                --kind)
                    if [[ "$command" == "get-bytes" ]]; then
                        COMPREPLY=( $(compgen -W "{{byteKinds}}" -- "$cur") )
                    else
                        COMPREPLY=( $(compgen -W "{{collectKinds}}" -- "$cur") )
                    fi
                    return 0
                    ;;
                --source)
                    if [[ "$command" == "inspect-heap" ]]; then
                        COMPREPLY=( $(compgen -W "{{heapSources}}" -- "$cur") )
                        return 0
                    fi
                    ;;
                --dump-type)
                    COMPREPLY=( $(compgen -W "{{dumpTypes}}" -- "$cur") )
                    return 0
                    ;;
                --asset)
                    COMPREPLY=( $(compgen -W "{{byteAssets}}" -- "$cur") )
                    return 0
                    ;;
                --depth)
                    COMPREPLY=( $(compgen -W "{{BashWords(CliCommandCatalog.DepthValues)}}" -- "$cur") )
                    return 0
                    ;;
                --mode)
                    COMPREPLY=( $(compgen -W "{{BashWords(CliCommandCatalog.CompareModes)}}" -- "$cur") )
                    return 0
                    ;;
            esac

            # A value-taking flag with no enum candidates was matched above; do not fall
            # through to option completion (that would offer flags as the flag's value).
            if [[ " $value_flags " == *" $prev "* ]]; then
                case "$prev" in
                    --save|--dump-file|--out|--symbol-path|--native-aot-map)
                        COMPREPLY=( $(compgen -f -- "$cur") )
                        ;;
                esac
                return 0
            fi

            if [[ -z "$command" ]]; then
                COMPREPLY=( $(compgen -W "{{commands}} {{globalOptions}}" -- "$cur") )
                return 0
            fi

            if [[ "$command" == "completion" ]]; then
                COMPREPLY=( $(compgen -W "{{shells}}" -- "$cur") )
                return 0
            fi

            case "$command" in
        {{BashCommandCases()}}
            esac

            COMPREPLY=( $(compgen -W "$opts {{globalOptions}}" -- "$cur") )
            return 0
        }

        complete -F _dotnet_diagnostics_complete dotnet-diagnostics
        complete -F _dotnet_diagnostics_complete dotnet-diagnostics-cli
        """;
    }

    private static string Zsh()
    {
        return $$"""
        #compdef dotnet-diagnostics dotnet-diagnostics-cli
        # zsh completion for dotnet-diagnostics.
        _dotnet_diagnostics()
        {
            local -a cli_commands global_options collect_kinds heap_sources byte_kinds byte_assets dump_types shells value_flags
            cli_commands=({{ZshWords(CliCommandCatalog.CommandNames)}})
            global_options=({{ZshWords(CliCommandCatalog.GlobalOptions)}})
            collect_kinds=({{ZshWords(CliCommands.CollectKinds)}})
            heap_sources=({{ZshWords(CliCommands.HeapSources)}})
            byte_kinds=({{ZshWords(CliCommands.ByteKinds)}})
            byte_assets=({{ZshWords(CliCommands.ByteAssets)}})
            dump_types=({{ZshWords(CliCommands.DumpTypes)}})
            shells=({{ZshWords(Shells)}})
            value_flags=({{ZshWords(CliCommandCatalog.ValueFlags)}})

            case ${words[CURRENT-1]} in
                --kind)
                    if (( ${words[(Ie)get-bytes]} )); then
                        _describe -t kinds 'get-bytes kind' byte_kinds
                    else
                        _describe -t kinds 'collect kind' collect_kinds
                    fi
                    return
                    ;;
                --source)
                    if (( ${words[(Ie)inspect-heap]} )); then
                        _describe -t sources 'heap source' heap_sources
                        return
                    fi
                    ;;
                --dump-type)
                    _describe -t dump-types 'dump type' dump_types
                    return
                    ;;
                --asset)
                    _describe -t assets 'module asset' byte_assets
                    return
                    ;;
                --depth)
                    _describe -t depths 'depth' '({{string.Join(' ', CliCommandCatalog.DepthValues)}})'
                    return
                    ;;
                --mode)
                    _describe -t modes 'mode' '({{string.Join(' ', CliCommandCatalog.CompareModes)}})'
                    return
                    ;;
            esac

            # A value-taking flag with no enum candidates was matched above; do not fall
            # through to option completion (that would offer flags as the flag's value).
            if (( ${value_flags[(Ie)${words[CURRENT-1]}]} )); then
                case ${words[CURRENT-1]} in
                    --save|--dump-file|--out|--symbol-path|--native-aot-map) _files ;;
                esac
                return
            fi

            local command=""
            for word in ${words[2,CURRENT-1]}; do
                if [[ "$word" != -* && ${cli_commands[(Ie)$word]} -ne 0 ]]; then
                    command="$word"
                    break
                fi
            done

            if [[ -z "$command" ]]; then
                _describe -t commands 'command' cli_commands
                _describe -t options 'global option' global_options
                return
            fi

            if [[ "$command" == "completion" ]]; then
                _describe -t shells 'shell' shells
                return
            fi

            local -a opts
            case "$command" in
        {{ZshCommandCases()}}
            esac
            _describe -t options 'option' opts
            _describe -t options 'global option' global_options
        }

        compdef _dotnet_diagnostics dotnet-diagnostics dotnet-diagnostics-cli
        """;
    }

    private static string Pwsh()
    {
        return $$"""
        # PowerShell completion for dotnet-diagnostics.
        Register-ArgumentCompleter -Native -CommandName 'dotnet-diagnostics', 'dotnet-diagnostics-cli' -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            $commands = {{PwshArray(CliCommandCatalog.CommandNames)}}
            $globalOptions = {{PwshArray(CliCommandCatalog.GlobalOptions)}}
            $collectKinds = {{PwshArray(CliCommands.CollectKinds)}}
            $heapSources = {{PwshArray(CliCommands.HeapSources)}}
            $byteKinds = {{PwshArray(CliCommands.ByteKinds)}}
            $byteAssets = {{PwshArray(CliCommands.ByteAssets)}}
            $dumpTypes = {{PwshArray(CliCommands.DumpTypes)}}
            $shells = {{PwshArray(Shells)}}
            $valueFlags = {{PwshArray(CliCommandCatalog.ValueFlags)}}
            $tokens = @($commandAst.CommandElements | ForEach-Object { $_.Extent.Text })
            $command = $null
            $skipNext = $false
            foreach ($token in ($tokens | Select-Object -Skip 1)) {
                if ($skipNext) {
                    $skipNext = $false
                    continue
                }
                if ($valueFlags -contains $token) {
                    $skipNext = $true
                    continue
                }
                if ($token -notlike '-*' -and $commands -contains $token) {
                    $command = $token
                    break
                }
            }

            $previous = if ($tokens.Count -gt 1) { $tokens[$tokens.Count - 2] } else { '' }
            $candidates = switch ($previous) {
                '--kind' {
                    if ($command -eq 'get-bytes') { $byteKinds } else { $collectKinds }
                    break
                }
                '--source' {
                    if ($command -eq 'inspect-heap') { $heapSources }
                    break
                }
                '--dump-type' { $dumpTypes; break }
                '--asset' { $byteAssets; break }
                '--depth' { {{PwshArray(CliCommandCatalog.DepthValues)}}; break }
                '--mode' { {{PwshArray(CliCommandCatalog.CompareModes)}}; break }
                default {
                    if ($valueFlags -contains $previous) {
                        # A value-taking flag with no enum candidates; offer file paths for
                        # path-like flags and nothing otherwise, never option flags.
                        if ($previous -in @('--save', '--dump-file', '--out', '--symbol-path', '--native-aot-map')) {
                            Get-ChildItem -Name -ErrorAction SilentlyContinue
                        }
                    } elseif ($null -eq $command) {
                        $commands + $globalOptions
                    } elseif ($command -eq 'completion') {
                        $shells
                    } else {
                        ({{PwshCommandMap()}})[$command] + $globalOptions
                    }
                }
            }

            $candidates |
                Where-Object { $_ -like "$wordToComplete*" } |
                ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
        }
        """;
    }

    private static string BashCommandCases()
    {
        var sb = new StringBuilder();
        foreach (var descriptor in CliCommandCatalog.CommandDescriptors)
        {
            sb.Append("        ")
                .Append(descriptor.Name)
                .Append(") opts=\"")
                .Append(BashWords(descriptor.CompletionOptions))
                .AppendLine("\" ;;");
        }

        return sb.ToString().TrimEnd();
    }

    private static string ZshCommandCases()
    {
        var sb = new StringBuilder();
        foreach (var descriptor in CliCommandCatalog.CommandDescriptors)
        {
            sb.Append("        ")
                .Append(descriptor.Name)
                .Append(") opts=(")
                .Append(ZshWords(descriptor.CompletionOptions))
                .AppendLine(") ;;");
        }

        return sb.ToString().TrimEnd();
    }

    private static string PwshCommandMap()
    {
        var sb = new StringBuilder("@{");
        foreach (var descriptor in CliCommandCatalog.CommandDescriptors)
        {
            sb.Append('\'')
                .Append(descriptor.Name)
                .Append("' = ")
                .Append(PwshArray(descriptor.CompletionOptions))
                .Append("; ");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string BashWords(IEnumerable<string> values) => string.Join(' ', values);

    private static string ZshWords(IEnumerable<string> values)
        => string.Join(' ', values.Select(static value => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'"));

    private static string PwshArray(IEnumerable<string> values)
        => "@(" + string.Join(", ", values.Select(static value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'")) + ")";
}

# Habilis Terminal shell integration for zsh (issue #268), the counterpart of HabilisTerminal.ps1.
#
# Sourced from our .zshrc after the user's own. It adds invisible OSC sequences around the prompt the user's setup
# made; the visible prompt is unchanged. Requests from Habilis Terminal arrive as a private key sequence (ESC [9024~),
# with the request itself in "$HABI_CHANNEL/request.zsh", written in zsh syntax.
#
#   OSC 133;A / 133;B        prompt starts / prompt ends, waiting for input
#   OSC 133;C / 133;D;code   command accepted / command finished
#   OSC 9;9;"path"           current folder
#   OSC 7731;habi;...        Habilis Terminal messages (ack, restore-pending, panel-key, command-line, goto, cd-path, open-address)

[[ -o interactive && -n $HABI_CHANNEL && -z $__habi_loaded ]] || return 0
typeset -g __habi_loaded=1 __habi_ran= __habi_saved= __habi_saved_cursor=0

__habi_osc() { builtin printf '\e]%s\a' "$1" }

# Base64 without a newline; the system's base64 is enough for the few lines sent.
__habi_b64() { builtin printf '%s' "$1" | command base64 | command tr -d '\n' }

__habi_folder() { __habi_osc "9;9;\"$PWD\"" }

__habi_precmd() {
    local code=$?
    if [[ -n $__habi_ran ]]; then
        __habi_osc "133;D;$code"
        __habi_ran=
    fi

    __habi_folder
    [[ -n $__habi_saved ]] && __habi_osc '7731;habi;restore-pending'

    # Around the prompt the user's setup made; a theme may build PS1 anew, so checked each time.
    if [[ $PS1 != *$'\e]133;A'* ]]; then
        PS1=$'%{\e]133;A\a%}'"$PS1"$'%{\e]133;B\a%}'
    fi
}

__habi_preexec() {
    __habi_ran=1
    __habi_osc "7731;habi;command-line;$(__habi_b64 "$1")"
    __habi_osc '133;C'
}

autoload -Uz add-zsh-hook
add-zsh-hook precmd __habi_precmd
add-zsh-hook preexec __habi_preexec

# The line goes into history and is cleared; Habilis Terminal does what it asks.
__habi_send_line() {
    builtin print -s -r -- "$BUFFER"
    BUFFER=
    __habi_osc "7731;habi;$1;$(__habi_b64 "$2")"
}

# Enter: "goto <place>" (issue #152), "cd" to a file as in Far (#245), plugin prefixes and commands (#49, #140); an empty
# line goes to the panel while it has the keys (#90). Anything else runs as before.
__habi_accept_line() {
    local line=$BUFFER
    if [[ -z ${line//[[:space:]]/} && -f $HABI_CHANNEL/panel-mode ]]; then
        __habi_osc '7731;habi;panel-key;Enter'
        return
    fi

    if [[ $line =~ '^[[:space:]]*goto[[:space:]]+(.*[^[:space:]])[[:space:]]*$' ]]; then
        __habi_send_line goto "$match[1]"
        return
    fi

    if [[ $line =~ '^[[:space:]]*(cd|chdir)[[:space:]]+(.*[^[:space:]])[[:space:]]*$' ]]; then
        local target=${(e)${(Q)match[2]}} 2>/dev/null
        [[ $target == '~'* ]] && target=$HOME${target#\~}
        if [[ -f $target ]]; then
            __habi_send_line cd-path "${target:A}"
            return
        fi
    fi

    local prefixes=$HABI_CHANNEL/prefixes commands=$HABI_CHANNEL/commands
    if [[ $line =~ '^[[:space:]]*([A-Za-z][A-Za-z0-9]*):' && -f $prefixes ]] && (( ${${(f)"$(<$prefixes)"}[(Ie)$match[1]]} )); then
        __habi_send_line open-address "${line##[[:space:]]#}"
        return
    fi

    if [[ $line =~ '^[[:space:]]*([^[:space:]]+)[[:space:]]+(["'"'"']?)(([A-Za-z][A-Za-z0-9]*):.*)$' && -f $commands ]]; then
        local address=${match[3]%%[[:space:]]#}
        [[ -n $match[2] ]] && address=${address%$match[2]}
        if (( ${${(f)"$(<$commands)"}[(Ie)$match[1] $match[4]]} )); then
            __habi_send_line open-address "$address"
            return
        fi
    fi

    zle .accept-line
}

zle -N accept-line __habi_accept_line

# While a panel has the keys, these keys on an empty line belong to it (issue #90).
__habi_panel_key() {
    if [[ -z $BUFFER && -f $HABI_CHANNEL/panel-mode ]]; then
        __habi_osc "7731;habi;panel-key;$1"
    else
        zle "$2"
    fi
}

__habi_left() { __habi_panel_key Left .backward-char }
__habi_right() { __habi_panel_key Right .forward-char }
__habi_home() { __habi_panel_key Home .beginning-of-line }
__habi_end() { __habi_panel_key End .end-of-line }
__habi_delete() { __habi_panel_key Delete .delete-char }
__habi_backspace() { __habi_panel_key Backspace .backward-delete-char }
# Tab keeps what the user's setup bound it to (fzf, oh-my-zsh), outside the panel's keys.
typeset -g __habi_tab_widget=${${(z)"$(bindkey '^I')"}[2]:-expand-or-complete}
[[ $__habi_tab_widget == __habi_tab ]] && __habi_tab_widget=expand-or-complete
__habi_tab() { __habi_panel_key Tab $__habi_tab_widget }
__habi_gray_add() { __habi_panel_key Add __habi_type_plus }
__habi_gray_subtract() { __habi_panel_key Subtract __habi_type_minus }
__habi_gray_multiply() { __habi_panel_key Multiply __habi_type_star }
__habi_type_plus() { LBUFFER+='+' }
__habi_type_minus() { LBUFFER+='-' }
__habi_type_star() { LBUFFER+='*' }

for __habi_widget in left right home end delete backspace tab gray_add gray_subtract gray_multiply type_plus type_minus type_star; do
    zle -N __habi_$__habi_widget
done
unset __habi_widget

# Requests: cd, run, insert, restore. The reply is an ack with the request's id.
__habi_request() {
    local file=$HABI_CHANNEL/request.zsh
    [[ -f $file ]] || return
    local habi_id= habi_op= habi_arg= habi_status=ok
    builtin source "$file"
    command rm -f -- "$file"
    case $habi_op in
        cd)
            if builtin cd -- "$habi_arg" 2>/dev/null; then
                local hook
                for hook in $precmd_functions; do
                    [[ $hook == __habi_precmd ]] && continue
                    "$hook" 2>/dev/null
                done
                __habi_folder
                zle reset-prompt
            else
                habi_status="error;cannot go to $habi_arg"
            fi
            ;;
        run)
            [[ -n $BUFFER ]] && { __habi_saved=$BUFFER; __habi_saved_cursor=$CURSOR }
            BUFFER=$habi_arg
            zle .accept-line
            ;;
        insert)
            LBUFFER+=$habi_arg
            ;;
        restore)
            if [[ -n $__habi_saved ]]; then
                BUFFER=$__habi_saved
                CURSOR=$__habi_saved_cursor
                __habi_saved=
            fi
            ;;
        *)
            habi_status="error;unknown request '$habi_op'"
            ;;
    esac
    __habi_osc "7731;habi;ack;$habi_id;$habi_status"
}

zle -N __habi_request

# Esc (the window sends it as ESC [9028~, issue #270), as in HabilisTerminal.ps1: on an empty line, while a panel has
# the keys, it is the panel's Esc, which gives the keys to the command line when the panel has no use for it (#290,
# #313); inside the console, while the panels are shown, the keys go back to the panel; on a line with text the line is
# cleared, as in Far.
__habi_escape() {
    if [[ -z $BUFFER && -f $HABI_CHANNEL/panel-mode ]]; then
        __habi_osc '7731;habi;panel-key;Escape'
    elif [[ -z $BUFFER && -f $HABI_CHANNEL/panels-shown ]]; then
        __habi_osc '7731;habi;focus-panel'
    else
        BUFFER=
    fi
}

zle -N __habi_escape

for __habi_keymap in emacs viins vicmd; do
    bindkey -M $__habi_keymap '\e[9024~' __habi_request
    bindkey -M $__habi_keymap '\e[9028~' __habi_escape
    bindkey -M $__habi_keymap '\e[9025~' __habi_gray_add
    bindkey -M $__habi_keymap '\e[9026~' __habi_gray_subtract
    bindkey -M $__habi_keymap '\e[9027~' __habi_gray_multiply
    bindkey -M $__habi_keymap '\e[D' __habi_left
    bindkey -M $__habi_keymap '\eOD' __habi_left
    bindkey -M $__habi_keymap '\e[C' __habi_right
    bindkey -M $__habi_keymap '\eOC' __habi_right
    bindkey -M $__habi_keymap '\e[H' __habi_home
    bindkey -M $__habi_keymap '\eOH' __habi_home
    bindkey -M $__habi_keymap '\e[F' __habi_end
    bindkey -M $__habi_keymap '\eOF' __habi_end
    bindkey -M $__habi_keymap '\e[3~' __habi_delete
    bindkey -M $__habi_keymap '^?' __habi_backspace
    bindkey -M $__habi_keymap '^I' __habi_tab
done
unset __habi_keymap

# A command the settings run first (shell.initCommand).
[[ -n $HABI_INIT ]] && eval "$HABI_INIT"

# Habilis Terminal shell integration for bash (issue #270): the file bash starts with (--rcfile). bash 3.2, which macOS
# keeps, has no PS0 and no READLINE_LINE, so this is the part that works there too: the prompt marks, the folder, the
# command lines and their exit codes, and cd from the panel. "goto", cd to a file and the panel's keys on an empty line
# are zsh's (habilis.zsh); run and insert from the panel are typed into the line by Habilis Terminal.
#
#   OSC 133;A / 133;B / 133;C / 133;D;code, OSC 9;9;"path", OSC 7731;habi;... as in HabilisTerminal.ps1.

# What a login bash runs: /etc/profile, then the first of the user's profiles (which usually read ~/.bashrc).
if [ -z "$HABI_NO_PROFILE" ]; then
    [ -f /etc/profile ] && . /etc/profile
    if [ -f "$HOME/.bash_profile" ]; then
        . "$HOME/.bash_profile"
    elif [ -f "$HOME/.bash_login" ]; then
        . "$HOME/.bash_login"
    elif [ -f "$HOME/.profile" ]; then
        . "$HOME/.profile"
    elif [ -f "$HOME/.bashrc" ]; then
        . "$HOME/.bashrc"
    fi
fi

# A command the settings run first (shell.initCommand).
[ -n "$HABI_INIT" ] && eval "$HABI_INIT"

if [ -z "$HABI_CHANNEL" ] || [ -z "$HABI_INTEGRATION" ] || [ -n "$__habi_loaded" ]; then
    return 0 2>/dev/null || true
fi
__habi_loaded=1
__habi_ready=
__habi_ran=

__habi_osc() { printf '\033]%s\007' "$1"; }

__habi_folder() { __habi_osc "9;9;\"$PWD\""; }

# Last in PROMPT_COMMAND; the status is taken first, before the user's own prompt commands run.
__habi_prompt() {
    if [ -n "$__habi_ran" ]; then
        __habi_osc "133;D;$__habi_status"
        __habi_ran=
    fi

    __habi_folder
    case "$PS1" in
        *'133;A'*) ;;
        *) PS1='\[\033]133;A\007\]'"$PS1"'\[\033]133;B\007\]' ;;
    esac
    __habi_ready=1
}

# The first command after a prompt is the user's: its line, then 133;C.
__habi_debug() {
    [ "$__habi_ready" = 1 ] || return 0
    case "$BASH_COMMAND" in
        '__habi_status=$?'*) return 0 ;; # Enter on an empty line: only the prompt runs
        __habi_request*) return 0 ;; # a request from Habilis Terminal (bind -x), not the user's command
    esac
    __habi_ready=
    __habi_ran=1
    local line
    line=$(HISTTIMEFORMAT= builtin history 1 | sed 's/^ *[0-9]* *//')
    __habi_osc "7731;habi;command-line;$(printf '%s' "$line" | base64 | tr -d '\n')"
    __habi_osc '133;C'
}

PROMPT_COMMAND="__habi_status=\$?${PROMPT_COMMAND:+; $PROMPT_COMMAND}; __habi_prompt"
trap '__habi_debug' DEBUG

# Requests from Habilis Terminal (cd only; see above): Ctrl+^ with the request in request.zsh, plain assignments. bash 3.2
# runs a bind -x only from a single key: on a sequence after ESC it fails with "cannot find keymap for command".
__habi_request() {
    local file="$HABI_CHANNEL/request.zsh" habi_id= habi_op= habi_arg= habi_status=ok
    [ -f "$file" ] || return 0
    . "$file"
    rm -f -- "$file"
    if [ "$habi_op" = cd ]; then
        if builtin cd -- "$habi_arg" 2>/dev/null; then
            __habi_folder
        else
            habi_status="error;cannot go to $habi_arg"
        fi
    else
        habi_status="error;unknown request '$habi_op'"
    fi
    __habi_osc "7731;habi;ack;$habi_id;$habi_status"
}

bind -x '"\C-^": __habi_request' 2>/dev/null

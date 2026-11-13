# Habilis Terminal (issue #268): zsh reads its startup files from ZDOTDIR, which points here, so the integration can
# run after the user's .zshrc. Each of these files runs the user's own first, from the user's ZDOTDIR or home.
: ${HABI_USER_ZDOTDIR:=$HOME}
if [[ -f $HABI_USER_ZDOTDIR/.zshenv ]]; then
    __habi_zdotdir=$ZDOTDIR
    ZDOTDIR=$HABI_USER_ZDOTDIR
    builtin source "$HABI_USER_ZDOTDIR/.zshenv"
    HABI_USER_ZDOTDIR=$ZDOTDIR # the user's .zshenv may move it
    ZDOTDIR=$__habi_zdotdir
    unset __habi_zdotdir
fi

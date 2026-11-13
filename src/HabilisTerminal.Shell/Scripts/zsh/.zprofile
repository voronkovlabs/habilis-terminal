# Habilis Terminal (issue #268): the user's .zprofile, as their ZDOTDIR would run it.
if [[ -f $HABI_USER_ZDOTDIR/.zprofile ]]; then
    __habi_zdotdir=$ZDOTDIR
    ZDOTDIR=$HABI_USER_ZDOTDIR
    builtin source "$HABI_USER_ZDOTDIR/.zprofile"
    ZDOTDIR=$__habi_zdotdir
    unset __habi_zdotdir
fi

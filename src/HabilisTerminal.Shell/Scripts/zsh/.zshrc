# Habilis Terminal (issue #268): the user's .zshrc, then the integration, which wraps what it set up.
__habi_zdotdir=$ZDOTDIR
if [[ -f $HABI_USER_ZDOTDIR/.zshrc ]]; then
    ZDOTDIR=$HABI_USER_ZDOTDIR
    builtin source "$HABI_USER_ZDOTDIR/.zshrc"
fi
ZDOTDIR=$__habi_zdotdir
unset __habi_zdotdir

[[ -n $HABI_INTEGRATION ]] && builtin source "$ZDOTDIR/habilis.zsh"

# A shell that is not a login shell reads no .zlogin: give the user's ZDOTDIR back now.
if [[ ! -o login ]]; then
    ZDOTDIR=$HABI_USER_ZDOTDIR
fi

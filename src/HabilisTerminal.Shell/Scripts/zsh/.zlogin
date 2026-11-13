# Habilis Terminal (issue #268): the user's .zlogin, then ZDOTDIR goes back to the user's for good, so zsh started
# from this one reads the user's files only.
ZDOTDIR=$HABI_USER_ZDOTDIR
[[ -f $ZDOTDIR/.zlogin ]] && builtin source "$ZDOTDIR/.zlogin"

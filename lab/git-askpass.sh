#!/usr/bin/env bash
# Answers git's questions for lab/sandbox.sh: the token is read from the environment, so it is never written to a file or to a command line.
case "$1" in
  Username*) echo "x-access-token" ;;
  *) echo "${LAB_GIT_TOKEN:?no token}" ;;
esac

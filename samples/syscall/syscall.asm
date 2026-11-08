ExitProcess PROTO
.code
NtEntry PROC
    sub rsp, 28h
    mov eax, 1000h
    syscall
    mov ecx, eax
    add ecx, 1
    call ExitProcess
    add rsp, 28h
    ret
NtEntry ENDP
END

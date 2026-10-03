.intel_syntax noprefix
.global _start
.section .text
_start:
    mov dword ptr [step], 1
    mov ebx, [esp + 8]
    mov eax, 5
    xor ecx, ecx
    xor edx, edx
    int 0x80
    test eax, eax
    js failed
    mov [descriptor], eax
    mov dword ptr [step], 2
    mov edi, eax
    mov eax, 192
    xor ebx, ebx
    mov ecx, 4096
    mov edx, 5
    mov esi, 2
    xor ebp, ebp
    int 0x80
    cmp eax, -4095
    jae failed
    mov [page], eax
    mov dword ptr [step], 3
    call eax
    cmp eax, 42
    jne failed
    mov dword ptr [step], 4
    mov eax, 125
    mov ebx, [page]
    mov ecx, 4096
    mov edx, 1
    int 0x80
    test eax, eax
    jne failed
    mov dword ptr [step], 5
    mov eax, 125
    mov edx, 5
    int 0x80
    test eax, eax
    jne failed
    call dword ptr [page]
    cmp eax, 42
    jne failed
    mov dword ptr [step], 6
    mov eax, 125
    mov ebx, [page]
    mov ecx, 4096
    mov edx, 7
    int 0x80
    test eax, eax
    jne failed
    mov ebx, [page]
    mov dword ptr [ebx + 1], 43
    call ebx
    cmp eax, 43
    jne failed
    mov dword ptr [step], 7
    mov eax, 91
    mov ebx, [page]
    mov ecx, 4096
    int 0x80
    test eax, eax
    jne failed
    mov dword ptr [step], 8
    mov eax, 192
    xor ebx, ebx
    mov ecx, 4096
    mov edx, 3
    mov esi, 1
    mov edi, [descriptor]
    xor ebp, ebp
    int 0x80
    cmp eax, -1
    jne failed
    mov dword ptr [step], 9
    mov eax, 192
    mov edx, 1
    int 0x80
    cmp eax, -4095
    jae failed
    mov [page], eax
    mov dword ptr [step], 10
    mov ebx, eax
    mov eax, 125
    mov edx, 3
    int 0x80
    cmp eax, -13
    jne failed
    mov eax, 91
    mov ebx, [page]
    int 0x80
    mov eax, 6
    mov ebx, [descriptor]
    int 0x80
    mov eax, 4
    mov ebx, 1
    lea ecx, [passed]
    mov edx, 27
    int 0x80
    mov eax, 1
    xor ebx, ebx
    int 0x80
failed:
    mov eax, 1
    mov ebx, [step]
    int 0x80
.section .data
descriptor: .long 0
page: .long 0
step: .long 0
passed: .ascii "Guest memory checks passed\n"
.section .note.GNU-stack,"",@progbits

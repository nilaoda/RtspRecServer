import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from "@/components/ui/dialog"

const Modal = ({
  open,
  title,
  onClose,
  children,
  wide,
  footer,
}: {
  open: boolean
  title: string
  onClose: () => void
  children: React.ReactNode
  wide?: boolean
  footer?: React.ReactNode
}) => {
  return (
    <Dialog open={open} onOpenChange={(val) => !val && onClose()}>
      <DialogContent className={wide ? "max-w-4xl" : "max-w-lg"}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        <div className="py-4">
            {children}
        </div>
        {footer && (
            <DialogFooter>
                {footer}
            </DialogFooter>
        )}
      </DialogContent>
    </Dialog>
  )
}

export default Modal

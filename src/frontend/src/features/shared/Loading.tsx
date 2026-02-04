import React from 'react';
import { Loader2 } from 'lucide-react';
import { cn } from '@/lib/utils';

interface LoadingProps {
  tip?: string;
  fullPage?: boolean;
}

const Loading: React.FC<LoadingProps> = ({ tip = '加载中...', fullPage = false }) => {
  const content = (
    <div className={cn(
      "flex flex-col items-center justify-center p-10 w-full gap-3",
      fullPage ? "h-screen" : "h-full min-h-[200px]"
    )}>
      <Loader2 className="h-8 w-8 animate-spin text-primary" />
      <span className="text-muted-foreground text-sm font-medium">{tip}</span>
    </div>
  );

  if (fullPage) {
    return (
      <div className="fixed inset-0 bg-background/80 backdrop-blur-sm z-[9999] flex items-center justify-center">
        {content}
      </div>
    );
  }

  return content;
};

export default Loading;
